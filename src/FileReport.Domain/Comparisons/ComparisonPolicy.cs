namespace FileReport.Domain.Comparisons;

public enum DifferenceKind { Added, Removed, Changed, Unchanged }

public static class ComparisonPolicy
{
    public static DifferenceKind Classify(ComparisonRecord? baseline, ComparisonRecord? candidate)
    {
        if (baseline is null && candidate is null)
        {
            throw new ArgumentException("At least one record is required.");
        }

        if (baseline is null)
        {
            return DifferenceKind.Added;
        }

        if (candidate is null)
        {
            return DifferenceKind.Removed;
        }

        if (!baseline.Key.Equals(candidate.Key))
        {
            throw new DomainException("KeyMismatch", "Shared records must have the same key.");
        }

        if (baseline.Values.Count != candidate.Values.Count)
        {
            throw new DomainException("SchemaMismatch", "Projected records must use the same columns.");
        }

        return baseline.Values.SequenceEqual(candidate.Values, StringComparer.Ordinal)
            ? DifferenceKind.Unchanged
            : DifferenceKind.Changed;
    }
}

public sealed class OrderedKeyValidator
{
    private CompositeKey? _previous;

    public void Accept(CompositeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var ordering = _previous?.CompareTo(key);
        if (ordering == 0)
        {
            throw new DomainException("DuplicateKey", "A key occurs more than once in the file.");
        }

        if (ordering > 0)
        {
            throw new DomainException("UnsortedInput", "Comparison input must be sorted by key.");
        }

        _previous = key;
    }
}
