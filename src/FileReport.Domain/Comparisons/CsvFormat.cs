namespace FileReport.Domain.Comparisons;

public sealed record CsvFormat
{
    public CsvFormat(char delimiter = ',')
    {
        if (delimiter is not (',' or ';' or '\t'))
        {
            throw new DomainException("InvalidDelimiter", "Use comma, semicolon, or tab.");
        }

        Delimiter = delimiter;
    }

    public char Delimiter { get; }
}
