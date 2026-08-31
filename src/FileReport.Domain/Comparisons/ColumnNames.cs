namespace FileReport.Domain.Comparisons;

internal static class ColumnNames
{
    public static IReadOnlyList<string> Copy(IEnumerable<string> names, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(names);
        var copy = names.ToArray();
        if ((!allowEmpty && copy.Length == 0) || copy.Any(string.IsNullOrEmpty))
        {
            throw new DomainException("InvalidColumns", "Column names must be nonempty.");
        }

        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new DomainException("DuplicateColumn", "Column names must be unique.");
        }

        return Array.AsReadOnly(copy);
    }
}
