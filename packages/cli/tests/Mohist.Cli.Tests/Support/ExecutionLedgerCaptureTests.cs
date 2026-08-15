using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Runner.Common;
using Xunit.Sdk;

namespace Mohist.Cli.Tests.Support;

public sealed class ExecutionLedgerCaptureTests
{
    private const string AssemblyId = "assembly";
    private const string CollectionId = "collection";
    private const string ClassId = "class";
    private const string MethodId = "method";
    private const string CaseId = "case";
    private const string TestId = "test";
    private const string TestName = "Mohist.Cli.Tests.Sample.Passes";
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> EmptyTraits =
        new Dictionary<string, IReadOnlyCollection<string>>();

    [Fact]
    public void Reporter_HasAnExplicitNativeCliSwitch()
    {
        var reporter = new ExecutionLedgerReporter();

        Assert.Equal("mohist-ledger", reporter.RunnerSwitch);
        Assert.True(reporter.CanBeEnvironmentallyEnabled);
    }

    [Fact]
    public async Task Reporter_UsesNativeExecutionTimeExcludingQueueDelay_AndReclaimsTerminalMetadata()
    {
        var queuedAt = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var startedAt = queuedAt.AddSeconds(5);
        var finishedAt = startedAt.AddSeconds(1);
        var (handler, artifacts) = CreateHandler();

        handler.OnMessage(AssemblyStarting(queuedAt));
        handler.OnMessage(CollectionStarting());
        handler.OnMessage(ClassStarting());
        handler.OnMessage(MethodStarting());
        handler.OnMessage(CaseStarting());
        handler.OnMessage(TestStarting(startedAt));
        handler.OnMessage(Passed(finishedAt, 0.01m));

        Assert.Equal(6, handler.CachedMetadataCount);
        handler.OnMessage(TestFinished(finishedAt));
        handler.OnMessage(CaseFinished());
        handler.OnMessage(MethodFinished());
        handler.OnMessage(ClassFinished());
        handler.OnMessage(CollectionFinished());
        handler.OnMessage(AssemblyFinished(finishedAt));
        Assert.Equal(0, handler.CachedMetadataCount);

        await handler.DisposeAsync();

        var result = Assert.Single(ReadDocument(artifacts).Cases);
        Assert.Equal(CaseId, result.TestCaseUid);
        Assert.Equal(0.01m, result.ExecutionTimeSeconds);
        Assert.Equal(startedAt, result.StartTime);
        Assert.Equal(finishedAt, result.FinishTime);
        Assert.Equal(TimeSpan.FromSeconds(5), result.StartTime - queuedAt);
        Assert.Equal(TimeSpan.FromSeconds(1), result.FinishTime - result.StartTime);
        Assert.Equal("Mohist.Cli.Tests.Sample", result.ClassName);
        Assert.Equal("CLI collection", result.CollectionName);
        Assert.Equal(new string('b', 64), ReadDocument(artifacts).SourceSha256);
        Assert.Equal("/virtual/Mohist.Cli.Tests.dll", Assert.Single(artifacts.ReadPaths));
        Assert.Equal("/virtual/cli.execution-ledger.json", artifacts.WrittenPath);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("skipped")]
    [InlineData("not-run")]
    public async Task Reporter_RecordsEveryNativeTerminalOutcome(string outcome)
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var finish = start.AddMilliseconds(20);
        var (handler, artifacts) = CreateHandler();
        handler.OnMessage(TestStarting(start));
        handler.OnMessage(Result(outcome, finish));
        handler.OnMessage(TestFinished(finish));

        await handler.DisposeAsync();

        Assert.Equal(outcome, Assert.Single(ReadDocument(artifacts).Cases).Outcome);
        Assert.Equal(0, handler.CachedMetadataCount);
    }

    [Fact]
    public void Capture_FailsClosedForDuplicateNamesAndZeroResults()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var duplicate = CreateCapture(2);
        duplicate.RecordStarting("one", "case-one", TestName, null, null, start);
        Assert.Throws<InvalidOperationException>(() =>
            duplicate.RecordStarting("two", "case-two", TestName, null, null, start));

        Assert.Throws<InvalidOperationException>(() => CreateCapture(1).ToDocument());
    }

    [Fact]
    public void Capture_AllowsRuntimeTheoryRowsToShareOneDiscoveredTestCase()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var capture = CreateCapture(1);
        capture.RecordStarting("test-one", CaseId, $"{TestName}(value: 1)", null, null, start);
        capture.RecordResult("test-one", "passed", 0.01m, start.AddMilliseconds(10));
        capture.RecordStarting("test-two", CaseId, $"{TestName}(value: 2)", null, null, start);
        capture.RecordResult("test-two", "passed", 0.02m, start.AddMilliseconds(20));

        var rows = capture.ToDocument().Cases;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(CaseId, row.TestCaseUid));
    }

    [Fact]
    public void Capture_RejectsResultWithoutStart_UnsettledTests_AndInvalidTiming()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var capture = CreateCapture(1);
        Assert.Throws<InvalidOperationException>(() =>
            capture.RecordResult("missing", "passed", 0.001m, start));

        capture.RecordStarting(TestId, CaseId, TestName, null, null, start);
        Assert.Throws<InvalidOperationException>(() => capture.ToDocument());

        var reversed = CreateCapture(1);
        reversed.RecordStarting(TestId, CaseId, TestName, null, null, start);
        Assert.Throws<InvalidOperationException>(() =>
            reversed.RecordResult(TestId, "passed", 0.001m, start.AddMilliseconds(-1)));
    }

    [Fact]
    public async Task DisposeAsync_ReclaimsEveryMetadataLevelAfterCancellationWithoutFinishedMessages()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var (handler, artifacts) = CreateHandler();
        StartEveryMetadataLevel(handler, start);
        Assert.Equal(6, handler.CachedMetadataCount);

        await handler.DisposeAsync();

        Assert.Equal(0, handler.CachedMetadataCount);
        Assert.Equal(string.Empty, artifacts.WrittenContent);
    }

    [Fact]
    public async Task DisposeAsync_ReclaimsEveryMetadataLevelAfterFailureWithoutFinishedMessages()
    {
        var start = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
        var (handler, artifacts) = CreateHandler();
        StartEveryMetadataLevel(handler, start);
        handler.OnMessage(Result("failed", start.AddMilliseconds(20)));
        Assert.Equal(6, handler.CachedMetadataCount);

        await handler.DisposeAsync();

        Assert.Equal(0, handler.CachedMetadataCount);
        Assert.Equal("failed", Assert.Single(ReadDocument(artifacts).Cases).Outcome);
    }

    private static (ExecutionLedgerReporterMessageHandler Handler, MemoryArtifactStore Artifacts) CreateHandler()
    {
        var assemblyBytes = new byte[] { 1, 2, 3, 4 };
        var assemblyHash = Convert.ToHexString(SHA256.HashData(assemblyBytes)).ToLowerInvariant();
        var runtime = new FakeRuntime(new Dictionary<string, string>
        {
            [ExecutionLedgerEnvironment.Path] = "/virtual/cli.execution-ledger.json",
            [ExecutionLedgerEnvironment.RunId] = "run-1",
            [ExecutionLedgerEnvironment.ManifestHash] = new string('a', 64),
            [ExecutionLedgerEnvironment.ManifestCount] = "1",
            [ExecutionLedgerEnvironment.AssemblyPath] = "/virtual/Mohist.Cli.Tests.dll",
            [ExecutionLedgerEnvironment.AssemblySha256] = assemblyHash,
            [ExecutionLedgerEnvironment.SourceSha256] = new string('b', 64),
            [ExecutionLedgerEnvironment.Parallelism] = "xunit-v3:parallel=collections;parallelAlgorithm=conservative;maxThreads=default",
        });
        var artifacts = new MemoryArtifactStore(assemblyBytes);
        return (new ExecutionLedgerReporterMessageHandler(runtime, artifacts), artifacts);
    }

    private static ExecutionLedgerCapture CreateCapture(int manifestCount) => new(new ExecutionLedgerMetadata(
        "run-1", new string('a', 64), manifestCount, "/virtual/test.dll", new string('b', 64),
        new string('c', 64), "3.2.2.0", "1.9.1.0", "xunit-v3:parallel=collections;parallelAlgorithm=conservative;maxThreads=default", "/virtual/ledger.json"));

    private static void StartEveryMetadataLevel(ExecutionLedgerReporterMessageHandler handler, DateTimeOffset start)
    {
        handler.OnMessage(AssemblyStarting(start));
        handler.OnMessage(CollectionStarting());
        handler.OnMessage(ClassStarting());
        handler.OnMessage(MethodStarting());
        handler.OnMessage(CaseStarting());
        handler.OnMessage(TestStarting(start));
    }

    private static ExecutionLedgerDocument ReadDocument(MemoryArtifactStore artifacts) =>
        JsonSerializer.Deserialize<ExecutionLedgerDocument>(
            artifacts.WrittenContent,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("reporter did not write a ledger document");

    private static TestAssemblyStarting AssemblyStarting(DateTimeOffset start) => new()
    {
        AssemblyUniqueID = AssemblyId,
        AssemblyName = "Mohist.Cli.Tests",
        AssemblyPath = "/virtual/Mohist.Cli.Tests.dll",
        ConfigFilePath = null,
        Seed = null,
        StartTime = start,
        TargetFramework = ".NETCoreApp,Version=v11.0",
        TestEnvironment = "fake",
        TestFrameworkDisplayName = "xUnit.net v3",
        Traits = EmptyTraits,
    };

    private static TestCollectionStarting CollectionStarting() => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestCollectionClassName = null,
        TestCollectionDisplayName = "CLI collection",
        Traits = EmptyTraits,
    };

    private static TestClassStarting ClassStarting() => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestClassName = "Mohist.Cli.Tests.Sample",
        TestClassNamespace = "Mohist.Cli.Tests",
        TestClassSimpleName = "Sample",
        Traits = EmptyTraits,
    };

    private static TestMethodStarting MethodStarting() => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        MethodArity = 0,
        MethodName = "Passes",
        Traits = EmptyTraits,
    };

    private static TestCaseStarting CaseStarting() => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        TestCaseUniqueID = CaseId,
        Explicit = false,
        SkipReason = null,
        SourceFilePath = null,
        SourceLineNumber = null,
        TestCaseDisplayName = TestName,
        TestClassMetadataToken = null,
        TestClassName = "Mohist.Cli.Tests.Sample",
        TestClassNamespace = "Mohist.Cli.Tests",
        TestClassSimpleName = "Sample",
        TestMethodArity = 0,
        TestMethodMetadataToken = null,
        TestMethodName = "Passes",
        TestMethodParameterTypesVSTest = null,
        TestMethodReturnTypeVSTest = null,
        Traits = EmptyTraits,
    };

    private static TestStarting TestStarting(DateTimeOffset start) => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        TestCaseUniqueID = CaseId,
        TestUniqueID = TestId,
        Explicit = false,
        StartTime = start,
        TestDisplayName = TestName,
        Timeout = 0,
        Traits = EmptyTraits,
    };

    private static TestPassed Passed(DateTimeOffset finish, decimal executionTime) => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        TestCaseUniqueID = CaseId,
        TestUniqueID = TestId,
        ExecutionTime = executionTime,
        FinishTime = finish,
        Output = string.Empty,
        Warnings = null,
    };

    private static IMessageSinkMessage Result(string outcome, DateTimeOffset finish) => outcome switch
    {
        "passed" => Passed(finish, 0.02m),
        "failed" => new TestFailed
        {
            AssemblyUniqueID = AssemblyId,
            TestCollectionUniqueID = CollectionId,
            TestClassUniqueID = ClassId,
            TestMethodUniqueID = MethodId,
            TestCaseUniqueID = CaseId,
            TestUniqueID = TestId,
            ExecutionTime = 0.02m,
            FinishTime = finish,
            Output = string.Empty,
            Warnings = null,
            Cause = FailureCause.Assertion,
            ExceptionParentIndices = [-1],
            ExceptionTypes = ["Xunit.Sdk.XunitException"],
            Messages = ["failed"],
            StackTraces = [null],
        },
        "skipped" => new TestSkipped
        {
            AssemblyUniqueID = AssemblyId,
            TestCollectionUniqueID = CollectionId,
            TestClassUniqueID = ClassId,
            TestMethodUniqueID = MethodId,
            TestCaseUniqueID = CaseId,
            TestUniqueID = TestId,
            ExecutionTime = 0m,
            FinishTime = finish,
            Output = string.Empty,
            Warnings = null,
            Reason = "synthetic reporter message",
        },
        "not-run" => new TestNotRun
        {
            AssemblyUniqueID = AssemblyId,
            TestCollectionUniqueID = CollectionId,
            TestClassUniqueID = ClassId,
            TestMethodUniqueID = MethodId,
            TestCaseUniqueID = CaseId,
            TestUniqueID = TestId,
            ExecutionTime = 0m,
            FinishTime = finish,
            Output = string.Empty,
            Warnings = null,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static TestFinished TestFinished(DateTimeOffset finish) => new()
    {
        AssemblyUniqueID = AssemblyId,
        TestCollectionUniqueID = CollectionId,
        TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        TestCaseUniqueID = CaseId,
        TestUniqueID = TestId,
        ExecutionTime = 0.01m,
        FinishTime = finish,
        Output = string.Empty,
        Warnings = null,
        Attachments = new Dictionary<string, TestAttachment>(),
    };

    private static TestCaseFinished CaseFinished() => new()
    {
        AssemblyUniqueID = AssemblyId, TestCollectionUniqueID = CollectionId, TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId, TestCaseUniqueID = CaseId,
        ExecutionTime = 0.01m, TestsFailed = 0, TestsNotRun = 0, TestsSkipped = 0, TestsTotal = 1,
    };

    private static TestMethodFinished MethodFinished() => new()
    {
        AssemblyUniqueID = AssemblyId, TestCollectionUniqueID = CollectionId, TestClassUniqueID = ClassId,
        TestMethodUniqueID = MethodId,
        ExecutionTime = 0.01m, TestsFailed = 0, TestsNotRun = 0, TestsSkipped = 0, TestsTotal = 1,
    };

    private static TestClassFinished ClassFinished() => new()
    {
        AssemblyUniqueID = AssemblyId, TestCollectionUniqueID = CollectionId, TestClassUniqueID = ClassId,
        ExecutionTime = 0.01m, TestsFailed = 0, TestsNotRun = 0, TestsSkipped = 0, TestsTotal = 1,
    };

    private static TestCollectionFinished CollectionFinished() => new()
    {
        AssemblyUniqueID = AssemblyId, TestCollectionUniqueID = CollectionId,
        ExecutionTime = 0.01m, TestsFailed = 0, TestsNotRun = 0, TestsSkipped = 0, TestsTotal = 1,
    };

    private static TestAssemblyFinished AssemblyFinished(DateTimeOffset finish) => new()
    {
        AssemblyUniqueID = AssemblyId, ExecutionTime = 0.01m, FinishTime = finish,
        TestsFailed = 0, TestsNotRun = 0, TestsSkipped = 0, TestsTotal = 1,
    };

    private sealed class FakeRuntime(IReadOnlyDictionary<string, string> environment) : IExecutionLedgerRuntime
    {
        public string AssemblyPath => "/virtual/Mohist.Cli.Tests.dll";
        public string? XunitVersion => "3.2.2.0";
        public string? MtpVersion => "1.9.1.0";
        public string? GetEnvironmentVariable(string name) => environment.GetValueOrDefault(name);
    }

    private sealed class MemoryArtifactStore(byte[] assemblyBytes) : IExecutionLedgerArtifactStore
    {
        public List<string> ReadPaths { get; } = [];
        public string WrittenPath { get; private set; } = string.Empty;
        public string WrittenContent { get; private set; } = string.Empty;

        public byte[] ReadAllBytes(string path)
        {
            ReadPaths.Add(path);
            return assemblyBytes;
        }

        public void WriteAllText(string path, string content)
        {
            WrittenPath = path;
            WrittenContent = content;
        }
    }
}
