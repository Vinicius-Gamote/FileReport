namespace FileReport.Domain.Comparisons;

public sealed class ComparisonRecord
{
    public ComparisonRecord(CompositeKey key, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new DomainException("InvalidValue", "CSV values are strings, not null values.");
        }

        Key = key;
        Values = Array.AsReadOnly(copy);
    }

    public CompositeKey Key { get; }
    public IReadOnlyList<string> Values { get; }
}
