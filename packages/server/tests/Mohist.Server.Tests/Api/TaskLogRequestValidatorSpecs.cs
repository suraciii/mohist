using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L0")]
public sealed class TaskLogRequestValidatorSpecs
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildValidatedLines_MapsValidEntriesWithoutChangingTheirOrder()
    {
        var entries = new List<TaskLogUploadEntry>
        {
            new() { Seq = 1, Timestamp = Timestamp, Source = "action", Text = "first" },
            new() { Seq = 2, Timestamp = Timestamp.AddSeconds(1), Source = "runner", Text = "second" },
        };

        var result = TaskLogRequestValidator.BuildValidatedLines(entries);

        Assert.Null(result.Error);
        Assert.Equal(
            new[] { (1L, "action", "first"), (2L, "runner", "second") },
            result.Lines.Select(line => (line.Seq, line.Source, line.Text)));
    }

    [Theory]
    [InlineData(0L, "Entry 0 seq must be positive")]
    [InlineData(-1L, "Entry 0 seq must be positive")]
    public void BuildValidatedLines_RejectsNonPositiveSequence(long seq, string expectedError)
    {
        var result = TaskLogRequestValidator.BuildValidatedLines(
            [new TaskLogUploadEntry { Seq = seq, Timestamp = Timestamp, Source = "action", Text = "line" }]);

        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void BuildValidatedLines_RejectsNonIncreasingSequences()
    {
        var result = TaskLogRequestValidator.BuildValidatedLines(
        [
            new TaskLogUploadEntry { Seq = 2, Timestamp = Timestamp, Source = "action", Text = "first" },
            new TaskLogUploadEntry { Seq = 2, Timestamp = Timestamp, Source = "action", Text = "duplicate" },
        ]);

        Assert.Equal("Entry seq values must be strictly increasing", result.Error);
    }

    [Fact]
    public void BuildValidatedLines_RejectsMissingTimestampSourceAndText()
    {
        Assert.Contains("timestamp", TaskLogRequestValidator.BuildValidatedLines(
            [new TaskLogUploadEntry { Seq = 1, Source = "action", Text = "line" }]).Error);
        Assert.Contains("source", TaskLogRequestValidator.BuildValidatedLines(
            [new TaskLogUploadEntry { Seq = 1, Timestamp = Timestamp, Text = "line" }]).Error);
        Assert.Contains("text", TaskLogRequestValidator.BuildValidatedLines(
            [new TaskLogUploadEntry { Seq = 1, Timestamp = Timestamp, Source = "action" }]).Error);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("text")]
    public void BuildValidatedLines_RejectsEntryFieldsOverTheirLimits(string field)
    {
        var entry = new TaskLogUploadEntry
        {
            Seq = 1,
            Timestamp = Timestamp,
            Source = field == "source" ? new string('s', TaskLogUploadLimits.MaxSourceLength + 1) : "action",
            Text = field == "text" ? new string('x', TaskLogUploadLimits.MaxTextLength + 1) : "line",
        };

        var result = TaskLogRequestValidator.BuildValidatedLines([entry]);

        Assert.Contains(field, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildValidatedLines_RejectsTotalTextOverTheBatchLimit()
    {
        var entries = Enumerable.Range(1, 31)
            .Select(seq => new TaskLogUploadEntry
            {
                Seq = seq,
                Timestamp = Timestamp,
                Source = "action",
                Text = new string('x', TaskLogUploadLimits.MaxTextLength),
            })
            .ToList();

        var result = TaskLogRequestValidator.BuildValidatedLines(entries);

        Assert.Contains("payload", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
