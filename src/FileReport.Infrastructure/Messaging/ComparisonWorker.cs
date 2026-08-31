using System.Diagnostics;
using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FileReport.Infrastructure.Messaging;

public sealed class ComparisonWorker(Broker broker, IJobRepository jobs, IComparisonEngine engine,
    IDbContextFactory<FileReportDbContext> factory, ProcessingSettings settings,
    ILogger<ComparisonWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await broker.Connect(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await broker.Declare(channel, stoppingToken);
                await channel.BasicQosAsync(0, (ushort)settings.PrefetchCount, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var reject = await Process(delivery.Body.ToArray(), stoppingToken);
                        if (reject) await channel.BasicRejectAsync(delivery.DeliveryTag, false, stoppingToken);
                        else await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Consumer disposition could not be persisted; delivery remains recoverable.");
                        // Bound re-delivery during a database outage. Never ACK an uncertain disposition.
                        await Task.Delay(5000, stoppingToken);
                        if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                    }
                };
                await channel.BasicConsumeAsync(Broker.Queue, false, consumer, stoppingToken);
                while (channel.IsOpen && !stoppingToken.IsCancellationRequested) await Task.Delay(1000, stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Worker dependency unavailable; reconnecting with bounded backoff.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
    public async Task<bool> Process(byte[] body, CancellationToken ct)
    {
        ComparisonCommand command;
        try
        {
            if (body.Length > 16384) return true;
            command = JsonData.Read<ComparisonCommand>(Encoding.UTF8.GetString(body));
            if (command.SchemaVersion != 1 || command.MessageId == Guid.Empty || command.JobId == Guid.Empty || command.AttemptNumber < 1) return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { return true; }
        using var activity = Telemetry.Source.StartActivity("comparison.process", ActivityKind.Consumer, command.TraceParent);
        activity?.SetTag("job.id", command.JobId);
        JobDocument document;
        try { document = await jobs.GetSystem(command.JobId, ct); }
        catch (RequestException e) when (e.Status == 404) { return true; }
        if (document.DeadLetterRequested) return true;
        if (document.Snapshot.InputVersion != command.InputVersion) return true;
        if (document.Snapshot.State is JobState.Succeeded or JobState.Failed) return false;
        long fence = 0;
        var claimed = await jobs.Mutate(command.JobId, null, m =>
        {
            if (m.Job.State is JobState.Succeeded or JobState.Failed || m.Job.NextAttemptNumber != command.AttemptNumber)
            { m.Notify = false; return false; }
            if (m.Job.State == JobState.Processing) { m.Notify = false; return false; }
            if (m.Job.RetryDueAtUtc > DateTimeOffset.UtcNow) { m.Notify = false; return false; }
            fence = checked((m.Job.CurrentAttempt?.FencingToken ?? 0) + 1);
            m.Job.StartAttempt(command.AttemptNumber, fence, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(settings.LeaseDurationSeconds));
            m.Document.Stage = "Validating"; return true;
        }, ct);
        if (!claimed) return false; // An active lease or a durable successor owns recovery.
        document = await jobs.GetSystem(command.JobId, ct);
        var watch = Stopwatch.StartNew();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execution.CancelAfter(TimeSpan.FromSeconds(settings.ExecutionTimeoutSeconds));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string stage = "Validating";
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.HeartbeatIntervalSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                    await jobs.Mutate(command.JobId, null, m =>
                    {
                        m.Job.RenewLease(fence, DateTimeOffset.UtcNow,
                        TimeSpan.FromSeconds(settings.LeaseDurationSeconds)); m.Document.Stage = Volatile.Read(ref stage); return true;
                    }, heartbeatStop.Token);
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch (Exception) { await execution.CancelAsync(); }
        }, CancellationToken.None);
        try
        {
            var result = await engine.Execute(document, fence, value => Volatile.Write(ref stage, value), execution.Token);
            await jobs.Mutate(command.JobId, null, m =>
            {
                m.Job.Complete(fence, result.Report.Counts, result.Report.BaselineRecords, result.Report.CandidateRecords, DateTimeOffset.UtcNow);
                m.Document.Report = result.Report; m.Document.Metrics.Add(result.Metrics); m.Document.Stage = "Completed";
                return true;
            }, ct);
            Telemetry.Completed.Add(1, new KeyValuePair<string, object?>("outcome", "Succeeded"));
            await Record(command, "Acknowledged", ct);
            return false;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var validation = e is DomainException domain && domain.Code is not ("LeaseExpired" or "StaleAttempt");
            var code = validation ? ((DomainException)e).Code : e is OperationCanceledException ? "ExecutionTimeout" : "InfrastructureFailure";
            bool reject = false;
            await jobs.Mutate(command.JobId, null, m =>
            {
                if (m.Job.State == JobState.Succeeded) return true; // Recovery after an uncertain success commit.
                if (validation) m.Job.Fail(fence, code, DateTimeOffset.UtcNow);
                else
                {
                    var due = RetryDue(command.AttemptNumber, settings);
                    m.Job.ScheduleRetry(fence, code, DateTimeOffset.UtcNow, due);
                    if (m.Job.State == JobState.RetryScheduled)
                        m.Commands.Add((new(Guid.NewGuid(), 1, command.JobId, m.Job.NextAttemptNumber, command.InputVersion, DateTimeOffset.UtcNow), due));
                    else { m.Document.DeadLetterRequested = true; reject = true; }
                }
                m.Document.Stage = m.Job.State == JobState.RetryScheduled ? "Retry scheduled" : "Failed";
                m.Document.Metrics.Add(new(command.AttemptNumber, m.Document.Files.Sum(f => f.Bytes),
                    null, null, null, null, null, watch.Elapsed.TotalSeconds, null, null, null, null,
                    settings.ResourceSamplingIntervalMilliseconds, 0, false, code, []));
                // Unavailable resource counters stay nullable in failed-attempt diagnostics, not invented zeros.
                return true;
            }, ct);
            Telemetry.Completed.Add(1, new KeyValuePair<string, object?>("outcome", code));
            await Record(command, reject ? "DeadLetterRequested" : "Acknowledged", ct);
            return reject;
        }
        finally
        {
            await heartbeatStop.CancelAsync(); await heartbeat;
            logger.LogInformation("Attempt ended for job {JobId}, attempt {Attempt}, elapsed {ElapsedSeconds}",
                command.JobId, command.AttemptNumber, watch.Elapsed.TotalSeconds);
        }
    }
    private async Task Record(ComparisonCommand command, string disposition, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Receipts" ("Id", "JobId", "Disposition", "AtUtc") VALUES ({command.MessageId}, {command.JobId}, {disposition}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("Id") DO UPDATE SET "Disposition" = EXCLUDED."Disposition", "AtUtc" = EXCLUDED."AtUtc"
            """, ct);
    }
    internal static DateTimeOffset RetryDue(int attempt, ProcessingSettings settings) => DateTimeOffset.UtcNow.AddSeconds(
        (settings.RetryDelaysSeconds.Length == 0 ? 0 : settings.RetryDelaysSeconds[Math.Clamp(attempt - 1, 0, settings.RetryDelaysSeconds.Length - 1)])
        + Random.Shared.Next(settings.RetryJitterMaxSeconds + 1));
}
