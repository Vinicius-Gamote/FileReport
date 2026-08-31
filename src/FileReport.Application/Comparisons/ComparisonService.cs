using System.Security.Cryptography;
using System.Text;
using FileReport.Application.Abstractions;
using FileReport.Application.Configuration;
using FileReport.Domain.Comparisons;

namespace FileReport.Application.Comparisons;

public sealed class ComparisonService(IJobRepository jobs, IFileStore files, ICsvPreview preview,
    ProcessingSettings settings, IClock clock)
{
    public Task<JobDocument> Create(Guid owner, string key, CancellationToken ct) => jobs.Create(owner, Key(key), ct);
    public Task<JobDocument> Get(Guid owner, Guid id, CancellationToken ct) => jobs.Get(id, owner, ct);
    public Task<HistoryPage> History(Guid owner, Guid? cursor, int limit, CancellationToken ct) =>
        jobs.History(owner, cursor, Math.Clamp(limit, 1, settings.MaxPageSize), ct);

    public async Task<JobDocument> Upload(Guid owner, Guid id, FileSide side, long revision,
        string name, Stream input, CancellationToken ct, Func<CancellationToken, Task>? validateCompletion = null)
    {
        name = name.Replace('\\', '/').Split('/').Last();
        if (name.Length is 0 or > 255 || name.Any(char.IsControl))
            throw new RequestException("InvalidFilename", "Choose a valid filename.");
        var generation = await jobs.Mutate(id, owner, m =>
        {
            m.Document.FirstUploadAtUtc ??= clock.UtcNow;
            m.Document.Stage = "Receiving file";
            m.Document.UploadLeases[side] = clock.UtcNow.AddSeconds(settings.UploadTimeoutSeconds);
            return m.Job.BeginUpload(side, revision);
        }, ct);
        var fileId = Guid.NewGuid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.UploadTimeoutSeconds));
        try
        {
            var progressWatch = System.Diagnostics.Stopwatch.StartNew();
            var stored = await files.Write(fileId, input, settings.MaxFileBytes, async received =>
            {
                if (progressWatch.Elapsed.TotalSeconds < settings.ProgressIntervalSeconds) return;
                progressWatch.Restart();
                await jobs.Mutate(id, owner, m =>
                {
                    m.Job.RecordUploadProgress(side, generation);
                    m.Document.ServerReceivedBytes = m.Document.Files.Where(f => f.Side != side).Sum(f => f.Bytes) + received;
                    return true;
                }, timeout.Token);
            }, timeout.Token);
            if (validateCompletion != null) await validateCompletion(timeout.Token);
            await jobs.Mutate(id, owner, m =>
            {
                m.Job.StoreFile(side, generation, stored);
                m.Document.UploadLeases.Remove(side);
                m.Document.Files.RemoveAll(f => f.Side == side);
                m.Document.Files.Add(new(fileId, side, name, stored.ByteLength, stored.Sha256,
                    clock.UtcNow, clock.UtcNow.AddDays(settings.SourceRetentionDays)));
                m.Document.LastUploadAtUtc = clock.UtcNow;
                m.Document.ServerReceivedBytes = m.Document.Files.Sum(f => f.Bytes);
                m.Document.Stage = m.Job.State == JobState.Ready ? "Ready to submit" : "Waiting for files";
                return true;
            }, ct);
        }
        catch
        {
            // A finalized object with an uncertain DB commit is left to orphan reconciliation.
            try { await jobs.Mutate(id, owner, m => { m.Job.FailUpload(side, generation); m.Document.UploadLeases.Remove(side); return true; }, CancellationToken.None); }
            catch (Exception) { /* Recovery also expires abandoned drafts; never mask the original failure. */ }
            throw;
        }
        return await jobs.Get(id, owner, ct);
    }

    public async Task<Dictionary<string, string[]>> Headers(Guid owner, Guid id, char baseline, char candidate, CancellationToken ct)
    {
        _ = new CsvFormat(baseline); _ = new CsvFormat(candidate);
        var doc = await jobs.Get(id, owner, ct);
        var result = new Dictionary<string, string[]>();
        foreach (var f in doc.Files)
        {
            if (f.ExpiresAtUtc <= clock.UtcNow) throw new RequestException("SourceExpired", "The source file has expired.", 410);
            using var stream = files.Open(f.Id);
            result[f.Side.ToString()] = await preview.Headers(stream, f.Side == FileSide.Baseline ? baseline : candidate, ct);
        }
        return result;
    }

    public async Task<JobDocument> Options(Guid owner, Guid id, long revision, string[] keys,
        string[]? columns, char baseline, char candidate, CancellationToken ct)
    {
        var options = new ComparisonOptions(keys, columns, new(baseline), new(candidate));
        var headers = await Headers(owner, id, baseline, candidate, ct);
        if (headers.Count != 2) throw new RequestException("FilesRequired", "Upload both files first.", 409);
        _ = new ComparisonSchema(headers["Baseline"], headers["Candidate"], options);
        await jobs.Mutate(id, owner, m => { m.Job.SetOptions(options, revision); return true; }, ct);
        return await jobs.Get(id, owner, ct);
    }

    public async Task<JobDocument> Submit(Guid owner, Guid id, long revision, string key, CancellationToken ct)
    {
        await jobs.Mutate(id, owner, m =>
        {
            if (m.Job.Revision != revision) throw new RequestException("RevisionConflict", "Reload the comparison.", 409);
            m.Job.Submit(clock.UtcNow, settings.MaxAttempts);
            m.Document.Stage = "Pending dispatch";
            m.Commands.Add((new(Guid.NewGuid(), 1, id, 1, m.Job.InputVersion!.Value, clock.UtcNow), clock.UtcNow));
            return true;
        }, ct, Key(key), "submit", Hash(revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return await jobs.Get(id, owner, ct);
    }

    public async Task<(Stream Stream, ArtifactMetadata Metadata)> Artifact(Guid owner, Guid id, Guid artifactId, CancellationToken ct)
    {
        var doc = await jobs.Get(id, owner, ct);
        var artifact = doc.Report?.Artifact;
        if (artifact is null || artifact.Id != artifactId) throw new RequestException("NotFound", "Report not found.", 404);
        if (artifact.ExpiresAtUtc <= clock.UtcNow) throw new RequestException("ArtifactExpired", "The report artifact has expired.", 410);
        try { return (files.Open(artifactId), artifact); }
        catch (FileNotFoundException) { throw new RequestException("ArtifactExpired", "The report artifact is unavailable.", 410); }
    }
    public static string Key(string key) => key.Length is >= 8 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        ? key : throw new RequestException("IdempotencyKeyRequired", "Supply an 8–128 character Idempotency-Key.");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
