namespace FileReport.Domain.Comparisons;

public sealed class ComparisonSchema
{
    private readonly IReadOnlyList<string> _baselineHeaders;
    private readonly IReadOnlyList<string> _candidateHeaders;
    private readonly int[] _baselineKeys;
    private readonly int[] _candidateKeys;
    private readonly int[] _baselineValues;
    private readonly int[] _candidateValues;

    public ComparisonSchema(
        IEnumerable<string> baselineHeaders,
        IEnumerable<string> candidateHeaders,
        ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _baselineHeaders = ColumnNames.Copy(baselineHeaders, allowEmpty: false);
        _candidateHeaders = ColumnNames.Copy(candidateHeaders, allowEmpty: false);
        if (!new HashSet<string>(_baselineHeaders, StringComparer.Ordinal).SetEquals(_candidateHeaders))
        {
            throw new DomainException("SchemaMismatch", "Both files must have the same header names.");
        }

        var nonKeyColumns = _baselineHeaders.Except(options.KeyColumns, StringComparer.Ordinal).ToArray();
        ComparedColumns = options.ComparedColumns ?? Array.AsReadOnly(nonKeyColumns);
        if ((nonKeyColumns.Length > 0 && ComparedColumns.Count == 0)
            || options.KeyColumns.Concat(ComparedColumns)
                .Any(column => !_baselineHeaders.Contains(column, StringComparer.Ordinal)))
        {
            throw new DomainException("InvalidColumns", "Select existing keys and comparison columns.");
        }

        _baselineKeys = Indexes(_baselineHeaders, options.KeyColumns);
        _candidateKeys = Indexes(_candidateHeaders, options.KeyColumns);
        _baselineValues = Indexes(_baselineHeaders, ComparedColumns);
        _candidateValues = Indexes(_candidateHeaders, ComparedColumns);
    }

    public IReadOnlyList<string> ComparedColumns { get; }

    public ComparisonRecord Project(FileSide side, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var headers = side switch
        {
            FileSide.Baseline => _baselineHeaders,
            FileSide.Candidate => _candidateHeaders,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };

        if (fields.Count != headers.Count)
        {
            throw new DomainException("MalformedRecord", "Record field count must match the header.");
        }

        var keyIndexes = side == FileSide.Baseline ? _baselineKeys : _candidateKeys;
        var valueIndexes = side == FileSide.Baseline ? _baselineValues : _candidateValues;
        return new ComparisonRecord(
            new CompositeKey(keyIndexes.Select(index => fields[index])),
            valueIndexes.Select(index => fields[index]));
    }

    private static int[] Indexes(IReadOnlyList<string> headers, IReadOnlyList<string> columns)
    {
        var positions = headers.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        return columns.Select(column => positions[column]).ToArray();
    }
}
