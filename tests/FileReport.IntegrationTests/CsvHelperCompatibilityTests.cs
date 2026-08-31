using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace FileReport.IntegrationTests;

// Library compatibility probes, not the production parser adapter or a benchmark.
public sealed class CsvHelperCompatibilityTests
{
    private static CsvConfiguration Configuration() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        IgnoreBlankLines = false,
        TrimOptions = TrimOptions.None,
        ExceptionMessagesContainRawData = false,
        MaxFieldSize = 128
    };

    [Fact]
    public async Task ReadsQuotedMultilineFieldsAndPreservesWhitespace()
    {
        using var reader = new StringReader("id,value\r\n1,\" first\r\nsecond \"\r\n2,\"a\"\"b\"\r\n");
        using var csv = new CsvReader(reader, Configuration());
        Assert.True(await csv.ReadAsync());
        csv.ReadHeader();
        Assert.True(await csv.ReadAsync());
        Assert.Equal(" first\r\nsecond ", csv.GetField(1));
        Assert.True(await csv.ReadAsync());
        Assert.Equal("a\"b", csv.GetField(1));
        Assert.False(await csv.ReadAsync());
    }

    [Fact]
    public async Task BlankRecordsAreNotSilentlySkipped()
    {
        using var reader = new StringReader("id\n\n1\n");
        using var csv = new CsvReader(reader, Configuration());
        Assert.True(await csv.ReadAsync());
        csv.ReadHeader();
        Assert.True(await csv.ReadAsync());
        Assert.Equal("", csv.GetField(0));
        Assert.True(await csv.ReadAsync());
        Assert.Equal("1", csv.GetField(0));
        Assert.False(await csv.ReadAsync());
    }

    [Fact]
    public async Task FieldLimitRaisesAnErrorWithoutEchoingRawValues()
    {
        var confidential = new string('z', 256);
        using var reader = new StringReader("id\n" + confidential);
        using var csv = new CsvReader(reader, Configuration());
        Assert.True(await csv.ReadAsync());
        csv.ReadHeader();
        var exception = await Assert.ThrowsAsync<MaxFieldSizeException>(() => csv.ReadAsync());
        Assert.DoesNotContain(confidential, exception.Message, StringComparison.Ordinal);
    }
}
