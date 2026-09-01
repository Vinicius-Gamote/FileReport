namespace FileReport.Domain.Comparisons;

public sealed class ComparisonOptions
{
    public ComparisonOptions(
        IEnumerable<string> keyColumns,
        IEnumerable<string>? comparedColumns = null,
        CsvFormat? baselineFormat = null,
        CsvFormat? candidateFormat = null)
    {
        KeyColumns = ColumnNames.Copy(keyColumns, allowEmpty: false);
        ComparedColumns = comparedColumns is null ? null : ColumnNames.Copy(comparedColumns, allowEmpty: true);
        if (ComparedColumns?.Intersect(KeyColumns, StringComparer.Ordinal).Any() == true)
        {
            throw new DomainException("InvalidColumns", "Key columns cannot also be comparison columns.");
        }

        BaselineFormat = baselineFormat ?? new CsvFormat();
        CandidateFormat = candidateFormat ?? new CsvFormat();
    }

    public const int ContractVersion = 2;
    public IReadOnlyList<string> KeyColumns { get; }
    public IReadOnlyList<string>? ComparedColumns { get; }
    public CsvFormat BaselineFormat { get; }
    public CsvFormat CandidateFormat { get; }
}
