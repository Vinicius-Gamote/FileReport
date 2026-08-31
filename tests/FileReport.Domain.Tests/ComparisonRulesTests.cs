using FileReport.Domain.Comparisons;

namespace FileReport.Domain.Tests;

public sealed class ComparisonRulesTests
{
    [Theory]
    [InlineData("a", "a", DifferenceKind.Unchanged)]
    [InlineData("a", "A", DifferenceKind.Changed)]
    [InlineData(" a", "a", DifferenceKind.Changed)]
    [InlineData("01", "1", DifferenceKind.Changed)]
    [InlineData("NULL", "", DifferenceKind.Changed)]
    [InlineData("", "", DifferenceKind.Unchanged)]
    [InlineData("é", "e\u0301", DifferenceKind.Changed)]
    public void StringSemanticsAreExact(string before, string after, DifferenceKind expected)
    {
        var baseline = new ComparisonRecord(new CompositeKey(["1"]), [before]);
        var candidate = new ComparisonRecord(new CompositeKey(["1"]), [after]);
        Assert.Equal(expected, ComparisonPolicy.Classify(baseline, candidate));
    }

    [Fact]
    public void MissingSidesHaveDirectionalMeaning()
    {
        var row = new ComparisonRecord(new CompositeKey(["1"]), ["value"]);
        Assert.Equal(DifferenceKind.Added, ComparisonPolicy.Classify(null, row));
        Assert.Equal(DifferenceKind.Removed, ComparisonPolicy.Classify(row, null));
        Assert.Throws<ArgumentException>(() => ComparisonPolicy.Classify(null, null));
    }

    [Fact]
    public void CompositeKeysCannotCollideThroughConcatenation()
    {
        var first = new CompositeKey(["a|b", "c"]);
        var second = new CompositeKey(["a", "b|c"]);
        Assert.NotEqual(first, second);
        Assert.NotEqual(0, first.CompareTo(second));
    }

    [Fact]
    public void KeysAndOptionsDoNotRetainMutableCallerArrays()
    {
        string[] parts = ["original"];
        string[] columns = ["id"];
        var key = new CompositeKey(parts);
        var options = new ComparisonOptions(columns);
        parts[0] = "changed";
        columns[0] = "changed";
        Assert.Equal("original", key.Components[0]);
        Assert.Equal("id", options.KeyColumns[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyKeyComponentsAreRejected(string? component)
    {
        Assert.Equal("EmptyKey", Assert.Throws<DomainException>(() => new CompositeKey([component!])).Code);
    }

    [Fact]
    public void IdenticalRepeatedKeysStillFailValidation()
    {
        var validator = new OrderedKeyValidator();
        validator.Accept(new CompositeKey(["1"]));
        validator.Accept(new CompositeKey(["2"]));
        Assert.Equal("DuplicateKey", Assert.Throws<DomainException>(() => validator.Accept(new CompositeKey(["2"]))).Code);
    }

    [Fact]
    public void DescendingInputCannotBeSilentlyCompared()
    {
        var validator = new OrderedKeyValidator();
        validator.Accept(new CompositeKey(["2"]));
        Assert.Equal("UnsortedInput", Assert.Throws<DomainException>(() => validator.Accept(new CompositeKey(["1"]))).Code);
    }

    [Fact]
    public void HeaderOrderAndUnselectedColumnsDoNotChangeEquality()
    {
        var schema = new ComparisonSchema(["id", "name", "ignored"], ["ignored", "name", "id"], new ComparisonOptions(["id"], ["name"]));
        var baseline = schema.Project(FileSide.Baseline, ["1", "Alice", "before"]);
        var candidate = schema.Project(FileSide.Candidate, ["after", "Alice", "1"]);
        Assert.Equal(DifferenceKind.Unchanged, ComparisonPolicy.Classify(baseline, candidate));
    }

    [Fact]
    public void ReorderedRecordsAreMatchedByKeysRatherThanPosition()
    {
        var schema = new ComparisonSchema(["id", "value"], ["id", "value"], new ComparisonOptions(["id"]));
        var baseline = new[] { schema.Project(FileSide.Baseline, ["1", "A"]), schema.Project(FileSide.Baseline, ["2", "B"]) };
        var candidate = new[] { schema.Project(FileSide.Candidate, ["2", "B"]), schema.Project(FileSide.Candidate, ["1", "A"]) };
        foreach (var row in baseline)
            Assert.Equal(DifferenceKind.Unchanged, ComparisonPolicy.Classify(row, candidate.Single(other => other.Key.Equals(row.Key))));
    }

    [Fact]
    public void KeyOnlySchemasHaveNoChangedRecords()
    {
        var schema = new ComparisonSchema(["id"], ["id"], new ComparisonOptions(["id"]));
        var baseline = schema.Project(FileSide.Baseline, ["1"]);
        var candidate = schema.Project(FileSide.Candidate, ["1"]);
        Assert.Empty(schema.ComparedColumns);
        Assert.Equal(DifferenceKind.Unchanged, ComparisonPolicy.Classify(baseline, candidate));
    }

    [Fact]
    public void MissingOrDuplicateHeadersAreRejected()
    {
        Assert.Equal("DuplicateColumn", Assert.Throws<DomainException>(() =>
            new ComparisonSchema(["id", "id"], ["id"], new ComparisonOptions(["id"]))).Code);
        Assert.Equal("SchemaMismatch", Assert.Throws<DomainException>(() =>
            new ComparisonSchema(["id"], ["ID"], new ComparisonOptions(["id"]))).Code);
        Assert.Equal("InvalidColumns", Assert.Throws<DomainException>(() =>
            new ComparisonSchema(["id"], ["id"], new ComparisonOptions(["missing"]))).Code);
    }

    [Fact]
    public void EmptyComparisonSubsetIsInvalidWhenValuesExist()
    {
        Assert.Throws<DomainException>(() =>
            new ComparisonSchema(["id", "value"], ["id", "value"], new ComparisonOptions(["id"], [])));
    }

    [Fact]
    public void FieldCountAndSharedKeyMismatchesAreRejected()
    {
        var schema = new ComparisonSchema(["id", "value"], ["id", "value"], new ComparisonOptions(["id"]));
        Assert.Throws<DomainException>(() => schema.Project(FileSide.Baseline, ["1"]));
        Assert.Throws<DomainException>(() => ComparisonPolicy.Classify(
            schema.Project(FileSide.Baseline, ["1", "A"]),
            schema.Project(FileSide.Candidate, ["2", "A"])));
    }

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('\t')]
    public void SupportedDelimitersAreExplicit(char delimiter)
    {
        Assert.Equal(delimiter, new CsvFormat(delimiter).Delimiter);
    }

    [Fact]
    public void UnsupportedDelimiterIsRejected()
    {
        Assert.Throws<DomainException>(() => new CsvFormat('|'));
    }

    [Fact]
    public void SuccessfulCountsMustReconcileAndCannotOverflow()
    {
        var summary = new ComparisonSummary(2, 3, 4, 5);
        summary.ValidateRecordCounts(12, 11);
        Assert.Throws<DomainException>(() => summary.ValidateRecordCounts(11, 12));
        Assert.Throws<DomainException>(() => new ComparisonSummary(-1, 0, 0, 0));
        Assert.Throws<OverflowException>(() => new ComparisonSummary(0, long.MaxValue, 1, 0).ValidateRecordCounts(0, 0));
        new ComparisonSummary(0, 0, 0, 0).ValidateRecordCounts(0, 0);
    }
}
