namespace Mohist.Cli.Tests.Support;

public sealed class ExecutionLedgerCaptureTests
{
    [Fact]
    public void ToJson_RecordsNativeExecutionTimeAndStableIdentityWithoutWallClockAccess()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var finish = start.AddMilliseconds(125);
        var capture = new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            "run-1",
            "manifest-1",
            1,
            "/virtual/Mohist.Cli.Tests.dll",
            "assembly-sha",
            "3.2.2.0",
            "1.9.1.0",
            "xunit-default",
            "/virtual/ledger.json"));

        capture.RecordStarting("test-1", "Mohist.Cli.Tests.Sample.Passes", "Mohist.Cli.Tests.Sample", "Test collection", start);
        capture.RecordResult("test-1", "passed", 0.125m, finish);

        var json = capture.ToJson();

        Assert.Contains("\"durationSource\":\"xunit.v3.ITestResultMessage.ExecutionTime\"", json);
        Assert.Contains("\"durationUnit\":\"seconds\"", json);
        Assert.Contains("\"executionTimeSeconds\":0.125", json);
        Assert.Contains("\"uid\":\"test-1\"", json);
        Assert.Contains("\"className\":\"Mohist.Cli.Tests.Sample\"", json);
    }

    [Fact]
    public void ToDocument_RejectsResultsWithoutAStartingMessage()
    {
        var capture = new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            "run-1", "manifest-1", 1, "/virtual/test.dll", "sha", "3.2.2.0", "1.9.1.0", "xunit-default", "/virtual/ledger.json"));

        Assert.Throws<InvalidOperationException>(() => capture.RecordResult(
            "missing", "passed", 0.001m, new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ToDocument_RejectsUnsettledTestsInsteadOfWritingPartialEvidence()
    {
        var capture = new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            "run-1", "manifest-1", 1, "/virtual/test.dll", "sha", "3.2.2.0", "1.9.1.0", "xunit-default", "/virtual/ledger.json"));
        capture.RecordStarting(
            "test-1",
            "Mohist.Cli.Tests.Sample.Passes",
            "Mohist.Cli.Tests.Sample",
            "Test collection",
            new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero));

        Assert.Throws<InvalidOperationException>(() => capture.ToDocument());
    }

    [Fact]
    public void RecordResult_RejectsUnsupportedOutcomeAndReversedTimestamps()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var capture = new ExecutionLedgerCapture(new ExecutionLedgerMetadata(
            "run-1", "manifest-1", 1, "/virtual/test.dll", "sha", "3.2.2.0", "1.9.1.0", "xunit-default", "/virtual/ledger.json"));
        capture.RecordStarting("test-1", "Mohist.Cli.Tests.Sample.Passes", null, null, start);

        Assert.Throws<InvalidOperationException>(() => capture.RecordResult(
            "test-1", "unknown", 0.001m, start.AddMilliseconds(1)));

        capture.RecordStarting("test-2", "Mohist.Cli.Tests.Sample.Passes", null, null, start);
        Assert.Throws<InvalidOperationException>(() => capture.RecordResult(
            "test-2", "passed", 0.001m, start.AddMilliseconds(-1)));
    }
}
