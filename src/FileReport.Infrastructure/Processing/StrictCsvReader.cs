using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;

namespace FileReport.Infrastructure.Processing;

// Source bytes are transcoded in a bounded stream before byte-level CSV framing.
public sealed class StrictCsvReader : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Encoding Windows1252 = CreateWindows1252();
    private readonly Stream _stream;
    private readonly char _delimiter;
    private readonly ProcessingSettings _settings;
    private readonly byte[] _buffer;
    private readonly Queue<int> _pending = new(3);
    private readonly bool _disposeStream;
    private readonly string _decodeFailureCode;
    private int _position, _length;
    private bool _started;
    public long BytesRead { get; private set; }
    public long LogicalRecords { get; private set; }

    public StrictCsvReader(Stream stream, CsvFormat format, ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        _settings = settings;
        _delimiter = format.Delimiter;
        _buffer = new byte[settings.IoBufferBytes];
        _decodeFailureCode = format.Encoding == CsvEncoding.Utf8 ? "InvalidUtf8" : "InvalidEncoding";
        _stream = format.Encoding switch
        {
            CsvEncoding.Utf8 => stream,
            CsvEncoding.Windows1252 => Encoding.CreateTranscodingStream(stream, Windows1252, Utf8, leaveOpen: true),
            CsvEncoding.Utf16LittleEndian => Encoding.CreateTranscodingStream(stream,
                new UnicodeEncoding(false, true, true), Utf8, leaveOpen: true),
            CsvEncoding.Utf16BigEndian => Encoding.CreateTranscodingStream(stream,
                new UnicodeEncoding(true, true, true), Utf8, leaveOpen: true),
            _ => throw Fault("InvalidEncoding")
        };
        _disposeStream = !ReferenceEquals(_stream, stream);
    }

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private async ValueTask<int> Next(CancellationToken ct)
    {
        if (_pending.TryDequeue(out var pending)) return pending;
        if (_position == _length)
        {
            try { _length = await _stream.ReadAsync(_buffer, ct); }
            catch (DecoderFallbackException) { throw Fault(_decodeFailureCode); }
            _position = 0;
            if (_length == 0) return -1;
        }
        BytesRead++; return _buffer[_position++];
    }
    public async Task<string[]?> Read(CancellationToken ct)
    {
        if (!_started)
        {
            _started = true;
            var first = await Next(ct);
            if (first == 0xEF)
            {
                var second = await Next(ct);
                var third = second == 0xBB ? await Next(ct) : -1;
                if (second != 0xBB || third != 0xBF)
                {
                    _pending.Enqueue(first);
                    if (second >= 0) _pending.Enqueue(second);
                    if (third >= 0) _pending.Enqueue(third);
                }
            }
            else if (first >= 0) _pending.Enqueue(first);
        }
        var fields = new List<string>();
        using var field = new MemoryStream();
        bool quoted = false, closed = false, hasData = false, fieldStart = true;
        int rawBytes = 0;
        void Append(int value)
        {
            if (field.Length >= _settings.MaxFieldBytes) throw Fault("FieldLimit");
            field.WriteByte((byte)value);
        }
        void FinishField()
        {
            if (fields.Count >= _settings.MaxColumns) throw Fault("ColumnLimit");
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
            if (++rawBytes > _settings.MaxRecordBytes) throw Fault("RecordLimit");
            if (quoted)
            {
                if (value == '"') { quoted = false; closed = true; }
                else Append(value);
                continue;
            }
            if (closed && value == '"') { Append(value); quoted = true; closed = false; continue; }
            if (value == _delimiter) { FinishField(); continue; }
            if (value is 10 or 13)
            {
                if (value == 13)
                {
                    if (await Next(ct) != 10) throw Fault("MalformedCsv");
                    if (++rawBytes > _settings.MaxRecordBytes) throw Fault("RecordLimit");
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
    public void Dispose()
    {
        if (_disposeStream) _stream.Dispose();
    }
    private static DomainException Fault(string code) => code is "InvalidUtf8" or "InvalidEncoding"
        ? new(code, "The selected source encoding could not decode the CSV. Choose the file's actual encoding or export it as UTF-8.")
        : new(code, "CSV validation failed. No cell content is included in diagnostics.");
}
public sealed class CsvPreview(ProcessingSettings settings) : ICsvPreview
{
    public async Task<string[]> Headers(Stream input, CsvFormat format, CancellationToken ct)
    {
        using var reader = new StrictCsvReader(input, format, settings);
        var header = await reader.Read(ct)
            ?? throw new DomainException("MissingHeader", "A nonempty CSV header is required.");
        if (header.Any(string.IsNullOrEmpty) || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            throw new DomainException("InvalidHeader", "Headers must be nonempty and unique.");
        return header;
    }
}
