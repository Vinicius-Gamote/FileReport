using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace FileReport.Infrastructure.Messaging;

public sealed class RecoveryService(IDbContextFactory<FileReportDbContext> factory, IJobRepository jobs,
    ProcessingSettings settings, ILogger<RecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await Recover(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { logger.LogWarning("Recovery dependency unavailable."); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
    public async Task Recover(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Jobs.AsNoTracking().Where(x => x.State == "Processing").OrderBy(x => x.CreatedAtUtc).Take(100).ToArrayAsync(ct);
        foreach (var row in rows)
        {
            var doc = JobRepository.Read(row);
            if (doc.Snapshot.Attempts.LastOrDefault()?.LeaseExpiresAtUtc > DateTimeOffset.UtcNow) continue;
            try
            {
                await jobs.Mutate(row.Id, null, m =>
                {
                    var due = ComparisonWorker.RetryDue(m.Job.NextAttemptNumber, settings);
                    m.Job.RecoverExpiredLease(DateTimeOffset.UtcNow, due);
                    m.Document.Metrics.Add(new(m.Job.CurrentAttempt!.Number, m.Document.Files.Sum(f => f.Bytes),
                        null, null, null, null, null, null, null, null, null, null,
                        settings.ResourceSamplingIntervalMilliseconds, 0, false, "LeaseExpired", []));
                    m.Document.Stage = m.Job.State == JobState.RetryScheduled ? "Recovering expired lease" : "Failed";
                    if (m.Job.State == JobState.RetryScheduled)
                        m.Commands.Add((new(Guid.NewGuid(), 1, row.Id, m.Job.NextAttemptNumber, m.Job.InputVersion!.Value, DateTimeOffset.UtcNow), due));
                    else
                    {
                        m.Document.DeadLetterRequested = true;
                        // A durable rejection command closes recovery even if the original delivery vanished.
                        m.Commands.Add((new(Guid.NewGuid(), 1, row.Id, m.Job.NextAttemptNumber, m.Job.InputVersion!.Value, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
                    }
                    return true;
                }, ct);
            }
            catch (DomainException e) when (e.Code == "LeaseStillActive") { }
        }
    }
}
