namespace FileReport.Application.Configuration;

public sealed class ProcessingSettings
{
    public const string SectionName = "Processing";
    public long MaxFileBytes { get; set; }
    public long MaxUserStorageBytes { get; set; }
    public int MaxConcurrentUploadsPerUser { get; set; }
    public int UploadTimeoutSeconds { get; set; }
    public int MaxMultipartHeadersBytes { get; set; }
    public int MaxColumns { get; set; }
    public int MaxFieldBytes { get; set; }
    public int MaxRecordBytes { get; set; }
    public int IoBufferBytes { get; set; }
    public int SortBufferBytes { get; set; }
    public int MergeFanIn { get; set; }
    public long MaxScratchBytes { get; set; }
    public long MaxReportBytes { get; set; }
    public int MaxSampleCount { get; set; }
    public int MaxSampleBytes { get; set; }
    public int MaxPageSize { get; set; }
    public int MaxConcurrentJobsPerWorker { get; set; }
    public int PrefetchCount { get; set; }
    public int MaxAttempts { get; set; }
    public int[] RetryDelaysSeconds { get; set; } = [];
    public int RetryJitterMaxSeconds { get; set; }
    public int ExecutionTimeoutSeconds { get; set; }
    public int ConsumerAcknowledgmentTimeoutSeconds { get; set; }
    public int LeaseDurationSeconds { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }
    public int ProgressIntervalSeconds { get; set; }
    public int ResourceSamplingIntervalMilliseconds { get; set; }
    public int PollingIntervalSeconds { get; set; }
    public int DraftRetentionHours { get; set; }
    public int SourceRetentionDays { get; set; }
    public int ReportRetentionDays { get; set; }
    public int OrphanGraceHours { get; set; }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        foreach (var property in typeof(ProcessingSettings).GetProperties())
        {
            var value = property.GetValue(this);
            if (property.Name == nameof(RetryJitterMaxSeconds))
            {
                if (RetryJitterMaxSeconds < 0) errors.Add("Processing:RetryJitterMaxSeconds cannot be negative.");
                continue;
            }

            if ((value is int integer && integer <= 0) || (value is long number && number <= 0))
            {
                errors.Add($"Processing:{property.Name} must be positive.");
            }
        }

        if (MaxUserStorageBytes < MaxFileBytes) errors.Add("User storage must accommodate at least one file.");
        if (MaxRecordBytes < MaxFieldBytes) errors.Add("The record limit must accommodate a field.");
        if (SortBufferBytes < MaxRecordBytes) errors.Add("The sort buffer must accommodate a record.");
        if (MergeFanIn < 2) errors.Add("Merge fan-in must be at least two.");
        if (PrefetchCount < MaxConcurrentJobsPerWorker) errors.Add("Prefetch must cover worker concurrency.");
        if (ConsumerAcknowledgmentTimeoutSeconds <= ExecutionTimeoutSeconds) errors.Add("Broker acknowledgment timeout must exceed execution timeout.");
        if (HeartbeatIntervalSeconds * 3L >= LeaseDurationSeconds) errors.Add("The lease must exceed three heartbeat intervals.");
        if (RetryDelaysSeconds is null || RetryDelaysSeconds.Length != MaxAttempts - 1 || RetryDelaysSeconds.Any(delay => delay <= 0))
            errors.Add("Provide one positive delay for each retry.");

        return errors.AsReadOnly();
    }
}
