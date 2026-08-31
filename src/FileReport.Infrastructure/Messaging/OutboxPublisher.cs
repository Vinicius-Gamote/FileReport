using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace FileReport.Infrastructure.Messaging;

public sealed class OutboxPublisher(IDbContextFactory<FileReportDbContext> factory, IJobRepository jobs,
    Broker broker, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await broker.Connect(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken);
                await broker.Declare(channel, stoppingToken);
                while (!stoppingToken.IsCancellationRequested && channel.IsOpen)
                {
                    var row = await Claim(stoppingToken);
                    if (row is null) { await Task.Delay(500, stoppingToken); continue; }
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(20));
                        await channel.BasicPublishAsync(Broker.Exchange, Broker.RoutingKey, mandatory: true,
                            new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = row.Id.ToString(), Type = "ComparisonRequested.v1" },
                            Encoding.UTF8.GetBytes(row.Payload), deadline.Token);
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        await db.Outbox.Where(x => x.Id == row.Id && x.Claim == row.Claim)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedAtUtc, DateTimeOffset.UtcNow)
                                .SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null), stoppingToken);
                        var command = JsonData.Read<ComparisonCommand>(row.Payload);
                        await jobs.Mutate(row.JobId, null, m =>
                        {
                            var changed = m.Job.MarkQueued(command.AttemptNumber);
                            m.Notify = changed; if (changed) m.Document.Stage = "Queued"; return true;
                        }, stoppingToken);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Command publication was not durably confirmed for message {MessageId}", row.Id);
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        await db.Outbox.Where(x => x.Id == row.Id && x.Claim == row.Claim && x.PublishedAtUtc == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null)
                                .SetProperty(x => x.AvailableAtUtc, DateTimeOffset.UtcNow.AddSeconds(10))
                                .SetProperty(x => x.Failures, x => x.Failures + 1), stoppingToken);
                    }
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Outbox dependency unavailable; committed commands remain pending.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
    public async Task<OutboxRow?> Claim(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var row = await db.Outbox.FromSqlInterpolated($"""
            SELECT * FROM "Outbox" WHERE "PublishedAtUtc" IS NULL AND "AvailableAtUtc" <= {now}
            AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" < {now})
            ORDER BY "AvailableAtUtc" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).FirstOrDefaultAsync(ct);
        if (row != null) { row.Claim = Guid.NewGuid(); row.LeaseUntilUtc = now.AddSeconds(60); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct); return row;
    }
}
