using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace FileReport.Infrastructure.Storage;

public sealed class RetentionService(IDbContextFactory<FileReportDbContext> factory, IJobRepository jobs,
    LocalFileStore store, ProcessingSettings settings, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Collect(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { logger.LogWarning("Retention is delayed; metadata and files remain recoverable."); }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
    public async Task Collect(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        Guid? cursor = null;
        while (true)
        {
            var query = db.Jobs.AsNoTracking().AsQueryable();
            if (cursor.HasValue) query = query.Where(x => x.Id.CompareTo(cursor.Value) > 0);
            var rows = await query.OrderBy(x => x.Id).Take(25).ToArrayAsync(ct);
            if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                var doc = JobRepository.Read(row);
                if (doc.Snapshot.State is JobState.Draft or JobState.Uploading or JobState.Ready)
                {
                    await jobs.Mutate(row.Id, null, m =>
                    {
                        foreach (var lease in m.Document.UploadLeases.Where(l => l.Value < now).ToArray())
                        {
                            var slot = m.Job.GetFileSlot(lease.Key);
                            if (slot.State == FileUploadState.Uploading) m.Job.FailUpload(lease.Key, slot.Generation);
                            m.Document.UploadLeases.Remove(lease.Key);
                        }
                        if (m.Document.UploadLeases.Count == 0 && m.Job.CreatedAtUtc < now.AddHours(-settings.DraftRetentionHours))
                            m.Job.Expire(now);
                        m.Notify = m.Job.Revision != row.Revision;
                        return true;
                    }, ct);
                    doc = await jobs.GetSystem(row.Id, ct);
                }
                if (doc.Snapshot.State is not (JobState.Succeeded or JobState.Failed or JobState.Expired)) continue;
                foreach (var file in doc.Files.Where(f => f.ExpiresAtUtc < now || doc.Snapshot.State == JobState.Expired))
                    TryDelete(store.ObjectPath(file.Id));
                if (doc.Report?.Artifact.ExpiresAtUtc < now) TryDelete(store.ObjectPath(doc.Report.Artifact.Id));
            }
            cursor = rows[^1].Id;
        }
        var cutoff = now.UtcDateTime.AddHours(-settings.OrphanGraceHours);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(store.Root, "temporary")))
            if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(store.Root, "objects")))
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) >= cutoff || !Guid.TryParseExact(Path.GetFileName(file), "N", out var id)) continue;
            var idText = id.ToString();
            var referenced = await db.Database.SqlQuery<long>($"""
                SELECT COUNT(*)::bigint AS "Value" FROM "Jobs" j WHERE
                j."Document"->'report'->'artifact'->>'id' = {idText}
                OR EXISTS (SELECT 1 FROM jsonb_array_elements(j."Document"->'files') f WHERE f->>'id' = {idText})
                """).SingleAsync(ct);
            if (referenced == 0) TryDelete(file);
        }
        foreach (var jobDirectory in Directory.EnumerateDirectories(Path.Combine(store.Root, "scratch")))
        {
            if ((File.GetAttributes(jobDirectory) & FileAttributes.ReparsePoint) != 0 ||
                !Guid.TryParseExact(Path.GetFileName(jobDirectory), "N", out var jobId)) continue;
            var active = await db.Jobs.AnyAsync(j => j.Id == jobId && (j.State == "Processing" || j.State == "RetryScheduled"), ct);
            if (active) continue;
            foreach (var attemptDirectory in Directory.EnumerateDirectories(jobDirectory))
            {
                if ((File.GetAttributes(attemptDirectory) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(attemptDirectory) >= cutoff) continue;
                foreach (var file in Directory.EnumerateFiles(attemptDirectory)) TryDelete(file);
                if (!Directory.EnumerateFileSystemEntries(attemptDirectory).Any()) Directory.Delete(attemptDirectory);
            }
        }
        // Delivery/idempotency/receipt records intentionally outlive transient notification rows.
        await db.Notifications.Where(x => x.SentAtUtc < now.AddDays(-1)).ExecuteDeleteAsync(ct);
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* Windows active readers deny deletion; retry on the next sweep. Unix open descriptors remain readable. */ }
    }
}
