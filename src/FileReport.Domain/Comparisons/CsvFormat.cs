namespace FileReport.Domain.Comparisons;

public enum CsvEncoding
{
    Utf8,
    Windows1252,
    Utf16LittleEndian,
    Utf16BigEndian
}

public sealed record CsvFormat
{
    public CsvFormat(char delimiter = ',', CsvEncoding encoding = CsvEncoding.Utf8)
    {
        if (delimiter is not (',' or ';' or '\t'))
        {
            throw new DomainException("InvalidDelimiter", "Use comma, semicolon, or tab.");
        }

        if (!Enum.IsDefined(encoding))
        {
            throw new DomainException("InvalidEncoding", "Select a supported CSV encoding.");
        }

        Delimiter = delimiter;
        Encoding = encoding;
    }

    public char Delimiter { get; }
    public CsvEncoding Encoding { get; }
}
