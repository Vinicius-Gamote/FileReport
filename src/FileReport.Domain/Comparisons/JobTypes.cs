namespace FileReport.Domain.Comparisons;

public enum FileSide { Baseline, Candidate }
public enum FileUploadState { Pending, Uploading, Stored, Failed }
public enum JobState { Draft, Uploading, Ready, PendingDispatch, Queued, Processing, RetryScheduled, Succeeded, Failed, Expired }

public sealed record StoredInput
{
    public StoredInput(Guid fileId, long byteLength, string sha256)
    {
        if (fileId == Guid.Empty || byteLength < 0
            || sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new DomainException("InvalidFileReference", "A valid immutable file reference is required.");
        }

        FileId = fileId;
        ByteLength = byteLength;
        Sha256 = sha256.ToLowerInvariant();
    }

    public Guid FileId { get; }
    public long ByteLength { get; }
    public string Sha256 { get; }
}

public sealed record FileSlotSnapshot(FileSide Side, long Generation, FileUploadState State, StoredInput? File);

public sealed record ProcessingAttempt(
    int Number,
    long FencingToken,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc,
    DateTimeOffset? FinishedAtUtc = null,
    string? FailureCode = null);
