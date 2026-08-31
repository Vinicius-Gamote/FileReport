namespace FileReport.Domain.Comparisons;

public sealed class ComparisonJob
{
    private readonly Dictionary<FileSide, FileSlotSnapshot> _slots = new()
    {
        [FileSide.Baseline] = new(FileSide.Baseline, 0, FileUploadState.Pending, null),
        [FileSide.Candidate] = new(FileSide.Candidate, 0, FileUploadState.Pending, null)
    };
    private readonly List<ProcessingAttempt> _attempts = [];
    private long _lastFencingToken;

    public ComparisonJob(Guid id, Guid ownerId, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty)
        {
            throw new DomainException("InvalidIdentity", "Job and owner identifiers are required.");
        }

        Id = id;
        OwnerId = ownerId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid OwnerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public JobState State { get; private set; } = JobState.Draft;
    public long Revision { get; private set; }
    public ComparisonOptions? Options { get; private set; }
    public int NextAttemptNumber { get; private set; } = 1;
    public int MaxAttempts { get; private set; }
    public long? InputVersion { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? TerminalAtUtc { get; private set; }
    public DateTimeOffset? RetryDueAtUtc { get; private set; }
    public string? FailureCode { get; private set; }
    public ComparisonSummary? Summary { get; private set; }
    public IReadOnlyList<ProcessingAttempt> Attempts => _attempts.AsReadOnly();
    public ProcessingAttempt? CurrentAttempt => _attempts.LastOrDefault();

    public FileSlotSnapshot GetFileSlot(FileSide side) => _slots.TryGetValue(side, out var slot)
        ? slot : throw new ArgumentOutOfRangeException(nameof(side));

    public JobSnapshot Capture() => new(Id, OwnerId, CreatedAtUtc, State, Revision,
        Options?.KeyColumns.ToArray(), Options?.ComparedColumns?.ToArray(),
        Options?.BaselineFormat.Delimiter ?? ',', Options?.CandidateFormat.Delimiter ?? ',',
        NextAttemptNumber, MaxAttempts, InputVersion, SubmittedAtUtc, TerminalAtUtc, RetryDueAtUtc,
        FailureCode, Summary, _slots.Values.ToArray(), _attempts.ToArray(), _lastFencingToken);

    // Only trusted, versioned persistence snapshots enter here; HTTP input never does.
    public static ComparisonJob Restore(JobSnapshot snapshot)
    {
        var job = new ComparisonJob(snapshot.Id, snapshot.OwnerId, snapshot.CreatedAtUtc)
        {
            State = snapshot.State,
            Revision = snapshot.Revision,
            Options = snapshot.Keys is null ? null : new ComparisonOptions(snapshot.Keys, snapshot.Columns,
                new CsvFormat(snapshot.BaselineDelimiter), new CsvFormat(snapshot.CandidateDelimiter)),
            NextAttemptNumber = snapshot.NextAttemptNumber,
            MaxAttempts = snapshot.MaxAttempts,
            InputVersion = snapshot.InputVersion,
            SubmittedAtUtc = snapshot.SubmittedAtUtc,
            TerminalAtUtc = snapshot.TerminalAtUtc,
            RetryDueAtUtc = snapshot.RetryDueAtUtc,
            FailureCode = snapshot.FailureCode,
            Summary = snapshot.Summary,
            _lastFencingToken = snapshot.LastFence
        };
        foreach (var slot in snapshot.Slots) job._slots[slot.Side] = slot;
        job._attempts.AddRange(snapshot.Attempts);
        return job;
    }

    public long BeginUpload(FileSide side, long expectedRevision)
    {
        RequireUnsubmitted();
        RequireRevision(expectedRevision);
        var slot = GetFileSlot(side);
        if (slot.State == FileUploadState.Uploading)
        {
            throw new DomainException("UploadInProgress", "A file upload already owns this slot.");
        }

        var generation = checked(slot.Generation + 1);
        _slots[side] = new(side, generation, FileUploadState.Uploading, null);
        State = JobState.Uploading;
        Revision++;
        return generation;
    }

    public void StoreFile(FileSide side, long generation, StoredInput file)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireUpload(side, generation);
        _slots[side] = new(side, generation, FileUploadState.Stored, file);
        State = _slots.Values.All(slot => slot.State == FileUploadState.Stored)
            ? JobState.Ready : JobState.Uploading;
        Revision++;
    }

    public void FailUpload(FileSide side, long generation)
    {
        RequireUpload(side, generation);
        _slots[side] = new(side, generation, FileUploadState.Failed, null);
        Revision++;
    }

    public void RecordUploadProgress(FileSide side, long generation)
    {
        RequireUpload(side, generation);
        Revision++;
    }

    public void SetOptions(ComparisonOptions options, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireUnsubmitted();
        RequireRevision(expectedRevision);
        Options = options;
        Revision++;
    }

    public void Submit(DateTimeOffset now, int maxAttempts)
    {
        if (State != JobState.Ready || Options is null)
        {
            throw new DomainException("ComparisonNotReady", "Both files and comparison options are required.");
        }

        RequireTime(now);
        if (maxAttempts < 1)
        {
            throw new DomainException("InvalidRetry", "At least one processing attempt is required.");
        }

        MaxAttempts = maxAttempts;
        InputVersion = Revision;
        SubmittedAtUtc = now.ToUniversalTime();
        State = JobState.PendingDispatch;
        Revision++;
    }

    public bool MarkQueued(int attemptNumber)
    {
        if (attemptNumber != NextAttemptNumber || State is not (JobState.PendingDispatch or JobState.RetryScheduled))
        {
            return false;
        }

        State = JobState.Queued;
        Revision++;
        return true;
    }

    public void StartAttempt(int attemptNumber, long fencingToken, DateTimeOffset now, TimeSpan leaseDuration)
    {
        RequireTime(now);
        if (State is not (JobState.PendingDispatch or JobState.Queued or JobState.RetryScheduled)
            || attemptNumber != NextAttemptNumber || fencingToken <= _lastFencingToken
            || (RetryDueAtUtc is not null && now < RetryDueAtUtc)
            || leaseDuration <= TimeSpan.Zero)
        {
            throw new DomainException("InvalidAttempt", "This attempt cannot acquire processing ownership.");
        }

        var attempt = new ProcessingAttempt(attemptNumber, fencingToken, now.ToUniversalTime(), now.Add(leaseDuration).ToUniversalTime());
        _lastFencingToken = fencingToken;
        _attempts.Add(attempt);
        State = JobState.Processing;
        RetryDueAtUtc = null;
        Revision++;
    }

    public void RenewLease(long fencingToken, DateTimeOffset now, TimeSpan leaseDuration)
    {
        var attempt = RequireLease(fencingToken, now);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var expiry = now.Add(leaseDuration).ToUniversalTime();
        _attempts[^1] = attempt with { LeaseExpiresAtUtc = expiry > attempt.LeaseExpiresAtUtc ? expiry : attempt.LeaseExpiresAtUtc };
        Revision++;
    }

    public void Complete(long fencingToken, ComparisonSummary summary, long baselineRecords, long candidateRecords, DateTimeOffset now)
    {
        var attempt = RequireLease(fencingToken, now);
        ArgumentNullException.ThrowIfNull(summary);
        summary.ValidateRecordCounts(baselineRecords, candidateRecords);
        _attempts[^1] = attempt with { FinishedAtUtc = now.ToUniversalTime() };
        Summary = summary;
        State = JobState.Succeeded;
        TerminalAtUtc = now.ToUniversalTime();
        Revision++;
    }

    public void Fail(long fencingToken, string failureCode, DateTimeOffset now)
    {
        var attempt = RequireLease(fencingToken, now);
        RequireFailureCode(failureCode);
        _attempts[^1] = attempt with { FinishedAtUtc = now.ToUniversalTime(), FailureCode = failureCode };
        SetFailed(failureCode, now);
    }

    public void ScheduleRetry(long fencingToken, string failureCode, DateTimeOffset now, DateTimeOffset dueAt)
    {
        var attempt = RequireLease(fencingToken, now);
        FinishForRetry(attempt, failureCode, now, dueAt);
    }

    public void RecoverExpiredLease(DateTimeOffset now, DateTimeOffset dueAt)
    {
        var attempt = CurrentAttempt;
        if (State != JobState.Processing || attempt is null || now < attempt.LeaseExpiresAtUtc)
        {
            throw new DomainException("LeaseStillActive", "Only an expired processing attempt can be recovered.");
        }

        FinishForRetry(attempt, "LeaseExpired", now, dueAt);
    }

    public void Expire(DateTimeOffset now)
    {
        RequireUnsubmitted();
        RequireTime(now);
        State = JobState.Expired;
        TerminalAtUtc = now.ToUniversalTime();
        Revision++;
    }

    private void FinishForRetry(ProcessingAttempt attempt, string failureCode, DateTimeOffset now, DateTimeOffset dueAt)
    {
        RequireFailureCode(failureCode);
        if (dueAt < now)
        {
            throw new DomainException("InvalidRetry", "Retry budget and due time must be valid.");
        }

        _attempts[^1] = attempt with { FinishedAtUtc = now.ToUniversalTime(), FailureCode = failureCode };
        if (attempt.Number >= MaxAttempts)
        {
            SetFailed(failureCode, now);
            return;
        }

        NextAttemptNumber = checked(attempt.Number + 1);
        RetryDueAtUtc = dueAt.ToUniversalTime();
        State = JobState.RetryScheduled;
        Revision++;
    }

    private void SetFailed(string failureCode, DateTimeOffset now)
    {
        FailureCode = failureCode;
        State = JobState.Failed;
        TerminalAtUtc = now.ToUniversalTime();
        Revision++;
    }

    private ProcessingAttempt RequireLease(long fencingToken, DateTimeOffset now)
    {
        var attempt = CurrentAttempt;
        if (State != JobState.Processing || attempt is null || attempt.FencingToken != fencingToken)
        {
            throw new DomainException("StaleAttempt", "Only the current attempt may write results.");
        }

        if (now < attempt.StartedAtUtc || now >= attempt.LeaseExpiresAtUtc)
        {
            throw new DomainException("LeaseExpired", "The processing lease is not active.");
        }

        return attempt;
    }

    private void RequireUpload(FileSide side, long generation)
    {
        RequireUnsubmitted();
        var slot = GetFileSlot(side);
        if (slot.State != FileUploadState.Uploading || slot.Generation != generation)
        {
            throw new DomainException("StaleUpload", "This upload no longer owns the file slot.");
        }
    }

    private void RequireUnsubmitted()
    {
        if (State is not (JobState.Draft or JobState.Uploading or JobState.Ready))
        {
            throw new DomainException("ImmutableComparison", "A submitted or expired comparison cannot be edited.");
        }
    }

    private void RequireRevision(long expectedRevision)
    {
        if (Revision != expectedRevision)
        {
            throw new DomainException("RevisionConflict", "The comparison has changed; reload its state.");
        }
    }

    private void RequireTime(DateTimeOffset now)
    {
        if (now < CreatedAtUtc || (SubmittedAtUtc is not null && now < SubmittedAtUtc))
        {
            throw new DomainException("InvalidTimestamp", "The operation cannot precede the comparison lifecycle.");
        }
    }

    private static void RequireFailureCode(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
    }
}
