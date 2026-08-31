using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain.Comparisons;
using Microsoft.EntityFrameworkCore;

namespace FileReport.Infrastructure.Persistence;

public sealed class JobRepository(IDbContextFactory<FileReportDbContext> factory, ProcessingSettings settings) : IJobRepository
{
    public async Task<JobDocument> Create(Guid ownerId, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(db, ownerId, ct);
        var prior = await db.Requests.SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.Operation == "create" && x.Key == key, ct);
        if (prior != null) return Read(await db.Jobs.SingleAsync(x => x.Id == prior.JobId, ct));
        if (await db.Jobs.CountAsync(x => x.OwnerId == ownerId && (x.State == "Draft" || x.State == "Uploading" || x.State == "Ready"), ct) >= 100)
            throw new RequestException("DraftQuota", "The active draft limit is reached.", 429);
        var job = new ComparisonJob(Guid.NewGuid(), ownerId, DateTimeOffset.UtcNow);
        var doc = new JobDocument { Snapshot = job.Capture() };
        db.Jobs.Add(Row(doc));
        db.Requests.Add(new() { Id = Guid.NewGuid(), OwnerId = ownerId, JobId = job.Id, Operation = "create", Key = key, Hash = "v1", Result = job.Id.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }
    public async Task<JobDocument> Get(Guid id, Guid ownerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return Read(await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct) ?? Missing());
    }
    public async Task<JobDocument> GetSystem(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return Read(await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? Missing());
    }
    public async Task<HistoryPage> History(Guid ownerId, Guid? cursor, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Jobs.AsNoTracking().Where(x => x.OwnerId == ownerId);
        if (cursor != null) query = query.Where(x => x.Id.CompareTo(cursor.Value) < 0);
        var rows = await query.OrderByDescending(x => x.Id).Take(limit + 1).ToArrayAsync(ct);
        return new(rows.Take(limit).Select(Read).ToArray(), rows.Length > limit ? rows[limit - 1].Id : null);
    }
    public async Task<T> Mutate<T>(Guid id, Guid? ownerId, Func<JobMutation, T> action, CancellationToken ct,
        string? idempotencyKey = null, string? operation = null, string? requestHash = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (ownerId.HasValue) await LockOwner(db, ownerId.Value, ct);
        var row = await db.Jobs.FromSqlInterpolated($"""SELECT * FROM "Jobs" WHERE "Id" = {id} FOR UPDATE""").SingleOrDefaultAsync(ct) ?? Missing();
        if (ownerId.HasValue && row.OwnerId != ownerId) throw new RequestException("NotFound", "Comparison not found.", 404);
        if (idempotencyKey is not null)
        {
            var prior = await db.Requests.SingleOrDefaultAsync(x => x.OwnerId == row.OwnerId && x.Operation == operation && x.Key == idempotencyKey, ct);
            if (prior != null)
            {
                if (prior.JobId != id || prior.Hash != requestHash) throw new RequestException("IdempotencyConflict", "The key was used for a different request.", 409);
                return JsonData.Read<T>(prior.Result);
            }
        }
        var mutation = new JobMutation(Read(row));
        var result = action(mutation);
        mutation.Document.Snapshot = mutation.Job.Capture();
        if (ownerId.HasValue && mutation.Document.Snapshot.Slots.Any(x => x.State == FileUploadState.Uploading))
        {
            // Serialize reservations per owner; reserve the maximum for every in-flight upload.
            var now = DateTimeOffset.UtcNow;
            var bytes = await db.Database.SqlQuery<long>($"""
                SELECT COALESCE(SUM((f->>'bytes')::bigint),0)::bigint AS "Value"
                FROM "Jobs" j, jsonb_array_elements(j."Document"->'files') f
                WHERE j."OwnerId" = {ownerId.Value} AND j."Id" != {id} AND (f->>'expiresAtUtc')::timestamptz > {now}
                """).SingleAsync(ct);
            var uploads = await db.Database.SqlQuery<long>($"""
                SELECT COUNT(*)::bigint AS "Value" FROM "Jobs" j,
                jsonb_array_elements(j."Document"->'snapshot'->'slots') s
                WHERE j."OwnerId" = {ownerId.Value} AND j."Id" != {id} AND s->>'state' = 'Uploading'
                """).SingleAsync(ct);
            bytes = checked(bytes + mutation.Document.Files.Where(f => f.ExpiresAtUtc > now).Sum(f => f.Bytes));
            uploads += mutation.Document.Snapshot.Slots.Count(s => s.State == FileUploadState.Uploading);
            if (uploads > settings.MaxConcurrentUploadsPerUser || bytes + uploads * settings.MaxFileBytes > settings.MaxUserStorageBytes)
                throw new RequestException("StorageQuota", "The upload or storage quota is reached.", 413);
        }
        row.Document = JsonData.Write(mutation.Document);
        row.State = mutation.Job.State.ToString(); row.Revision = mutation.Job.Revision;
        foreach (var item in mutation.Commands)
            db.Outbox.Add(new() { Id = item.Command.MessageId, JobId = id, Payload = JsonData.Write(item.Command), AvailableAtUtc = item.AvailableAt });
        if (mutation.Notify)
        {
            var ev = new JobEvent(Guid.NewGuid(), 1, id, row.OwnerId, row.Revision, row.State,
                mutation.Document.Stage, mutation.Job.NextAttemptNumber, mutation.Document.ServerReceivedBytes,
                DateTimeOffset.UtcNow, row.State == "Succeeded", mutation.Job.FailureCode);
            db.Notifications.Add(new() { Id = ev.EventId, JobId = id, Payload = JsonData.Write(ev), CreatedAtUtc = ev.AtUtc });
        }
        if (idempotencyKey != null)
            db.Requests.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerId = row.OwnerId,
                JobId = id,
                Operation = operation!,
                Key = idempotencyKey,
                Hash = requestHash!,
                Result = JsonData.Write(result)
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
    private static async Task LockOwner(FileReportDbContext db, Guid id, CancellationToken ct) =>
        _ = await db.Users.FromSqlInterpolated($"""SELECT * FROM "Users" WHERE "Id" = {id} FOR UPDATE""").SingleAsync(ct);
    private static JobRow Missing() => throw new RequestException("NotFound", "Comparison not found.", 404);
    public static JobDocument Read(JobRow row) => JsonData.Read<JobDocument>(row.Document);
    private static JobRow Row(JobDocument doc) => new()
    {
        Id = doc.Snapshot.Id,
        OwnerId = doc.Snapshot.OwnerId,
        CreatedAtUtc = doc.Snapshot.CreatedAtUtc,
        Revision = doc.Snapshot.Revision,
        State = doc.Snapshot.State.ToString(),
        Document = JsonData.Write(doc)
    };
}
