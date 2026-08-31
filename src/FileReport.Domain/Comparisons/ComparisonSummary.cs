namespace FileReport.Domain.Comparisons;

public sealed record ComparisonSummary
{
    public ComparisonSummary(long added, long removed, long changed, long unchanged)
    {
        if (added < 0 || removed < 0 || changed < 0 || unchanged < 0)
        {
            throw new DomainException("InvalidCount", "Record counts cannot be negative.");
        }

        Added = added;
        Removed = removed;
        Changed = changed;
        Unchanged = unchanged;
    }

    public long Added { get; }
    public long Removed { get; }
    public long Changed { get; }
    public long Unchanged { get; }

    public void ValidateRecordCounts(long baselineRecords, long candidateRecords)
    {
        if (baselineRecords != checked(Removed + Changed + Unchanged)
            || candidateRecords != checked(Added + Changed + Unchanged))
        {
            throw new DomainException("ResultCountMismatch", "Result counts do not reconcile with input records.");
        }
    }
}
