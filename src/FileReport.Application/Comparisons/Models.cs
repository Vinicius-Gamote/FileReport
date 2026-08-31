using FileReport.Domain.Comparisons;

namespace FileReport.Application.Comparisons;

public sealed class JobDocument
{
    public required JobSnapshot Snapshot { get; set; }
    public List<FileMetadata> Files { get; set; } = [];
    public Dictionary<FileSide, DateTimeOffset> UploadLeases { get; set; } = [];
    public DateTimeOffset? FirstUploadAtUtc { get; set; }
    public DateTimeOffset? LastUploadAtUtc { get; set; }
    public string Stage { get; set; } = "Waiting for files";
    public long ServerReceivedBytes { get; set; }
    public ReportData? Report { get; set; }
    public List<AttemptMetrics> Metrics { get; set; } = [];
    public bool DeadLetterRequested { get; set; }
    public DateTimeOffset? DeadLetterObservedAtUtc { get; set; }
}
public sealed record FileMetadata(Guid Id, FileSide Side, string Name, long Bytes, string Sha256,
    DateTimeOffset StoredAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed record ArtifactMetadata(Guid Id, long Bytes, string Sha256, DateTimeOffset ExpiresAtUtc);
public sealed record Difference(string Kind, string[] Key, string[]? Baseline, string[]? Candidate);
public sealed record ReportData(long BaselineRecords, long CandidateRecords, ComparisonSummary Counts,
    ArtifactMetadata Artifact, Difference[] Samples, bool SamplesTruncated, string[] ComparedColumns);
public sealed record AttemptMetrics(int Attempt, long UniqueInputBytes, long? PhysicalReadBytes,
    long? PhysicalWrittenBytes, long? ScratchPeakBytes, long? BaselineRecords, long? CandidateRecords,
    double? ElapsedSeconds, double? CpuSeconds, long? ObservedPeakWorkingSetBytes, long? ObservedPeakManagedHeapBytes,
    long? AllocatedBytes, int SampleIntervalMilliseconds, int Samples, bool Complete, string Outcome,
    Dictionary<string, double> StageSeconds);
public sealed record ComparisonCommand(Guid MessageId, int SchemaVersion, Guid JobId, int AttemptNumber,
    long InputVersion, DateTimeOffset CreatedAtUtc, string? TraceParent = null);
public sealed record JobEvent(Guid EventId, int SchemaVersion, Guid JobId, Guid OwnerId, long Revision,
    string State, string Stage, int Attempt, long ServerReceivedBytes, DateTimeOffset AtUtc,
    bool MetricsComplete, string? ErrorCode);
public sealed class JobMutation(JobDocument document)
{
    public JobDocument Document { get; } = document;
    public ComparisonJob Job { get; } = ComparisonJob.Restore(document.Snapshot);
    public List<(ComparisonCommand Command, DateTimeOffset AvailableAt)> Commands { get; } = [];
    public bool Notify { get; set; } = true;
}
public sealed record HistoryPage(JobDocument[] Items, Guid? NextCursor);
public sealed record IdentityResult(Guid Id, string Email, string Token, DateTimeOffset ExpiresAtUtc);
public sealed record EmailStatus(Guid Id, string State, string Recipient, string? ErrorCode, DateTimeOffset CreatedAtUtc);
public sealed class RequestException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
