using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;

namespace FileReport.Infrastructure.Processing;

// Byte-level framing bounds records before decoding or allocating their string representation.
public sealed class StrictCsvReader(Stream stream, char delimiter, ProcessingSettings settings)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _buffer = new byte[settings.IoBufferBytes];
    private int _position, _length;
    private bool _started;
    private int _pending = -1;
    public long BytesRead { get; private set; }
    public long LogicalRecords { get; private set; }

    private async ValueTask<int> Next(CancellationToken ct)
    {
        if (_pending >= 0) { var pending = _pending; _pending = -1; return pending; }
        if (_position == _length)
        {
            _length = await stream.ReadAsync(_buffer, ct); _position = 0;
            if (_length == 0) return -1;
        }
        BytesRead++; return _buffer[_position++];
    }
    public async Task<string[]?> Read(CancellationToken ct)
    {
        _ = new CsvFormat(delimiter);
        if (!_started)
        {
            _started = true;
            var first = await Next(ct);
            if (first == 0xEF)
            {
                if (await Next(ct) != 0xBB || await Next(ct) != 0xBF) throw Fault("InvalidUtf8");
            }
            else _pending = first;
        }
        var fields = new List<string>();
        using var field = new MemoryStream();
        bool quoted = false, closed = false, hasData = false, fieldStart = true;
        int rawBytes = 0;
        void Append(int value)
        {
            if (field.Length >= settings.MaxFieldBytes) throw Fault("FieldLimit");
            field.WriteByte((byte)value);
        }
        void FinishField()
        {
            if (fields.Count >= settings.MaxColumns) throw Fault("ColumnLimit");
            try { fields.Add(Utf8.GetString(field.GetBuffer(), 0, checked((int)field.Length))); }
            catch (DecoderFallbackException) { throw Fault("InvalidUtf8"); }
            field.SetLength(0); closed = false; fieldStart = true;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int value = await Next(ct);
            if (value < 0)
            {
                if (quoted) throw Fault("MalformedCsv");
                if (!hasData) return null;
                FinishField(); LogicalRecords++; return fields.ToArray();
            }
            hasData = true;
            if (++rawBytes > settings.MaxRecordBytes) throw Fault("RecordLimit");
            if (quoted)
            {
                if (value == '"') { quoted = false; closed = true; }
                else Append(value);
                continue;
            }
            if (closed && value == '"') { Append(value); quoted = true; closed = false; continue; }
            if (value == delimiter) { FinishField(); continue; }
            if (value is 10 or 13)
            {
                if (value == 13)
                {
                    if (await Next(ct) != 10) throw Fault("MalformedCsv");
                    if (++rawBytes > settings.MaxRecordBytes) throw Fault("RecordLimit");
                }
                FinishField(); LogicalRecords++; return fields.ToArray();
            }
            if (closed) throw Fault("MalformedCsv");
            if (value == '"')
            {
                if (!fieldStart) throw Fault("MalformedCsv");
                quoted = true; fieldStart = false; continue;
            }
            fieldStart = false; Append(value);
        }
    }
    private static DomainException Fault(string code) => new(code, "CSV validation failed. No cell content is included in diagnostics.");
}
public sealed class CsvPreview(ProcessingSettings settings) : ICsvPreview
{
    public async Task<string[]> Headers(Stream input, char delimiter, CancellationToken ct)
    {
        var header = await new StrictCsvReader(input, delimiter, settings).Read(ct)
            ?? throw new DomainException("MissingHeader", "A nonempty CSV header is required.");
        if (header.Any(string.IsNullOrEmpty) || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            throw new DomainException("InvalidHeader", "Headers must be nonempty and unique.");
        return header;
    }
}
