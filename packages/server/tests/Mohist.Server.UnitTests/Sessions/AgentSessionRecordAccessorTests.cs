using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

/// <summary>
/// Issue-370 T-002 / design D2: codifies the <see cref="AgentSessionRecord"/>
/// label-with-metadata-fallback and issue-number-parsing semantics as
/// unit-level scenarios. The fallback exists for synthetic records built
/// with a hand-crafted label dictionary (tests / fakes) — production
/// records are built with <c>session.Metadata.Labels</c>, so the two
/// dictionaries coincide and the fallback is a defensive no-op.
/// </summary>
public class AgentSessionRecordAccessorTests
{
    [Fact]
    public void Label_RecordValueWinsWhenBothDictionariesCarryTheKey()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.SessionName] = "from-record",
            },
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.SessionName] = "from-metadata",
            });

        Assert.Equal("from-record", record.Label(AgentSessionQueryMetadataKeys.SessionName));
    }

    [Fact]
    public void Label_FallsBackToMetadataWhenRecordDictionaryDoesNotCarryTheKey()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal),
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.SessionName] = "from-metadata",
            });

        Assert.Equal("from-metadata", record.Label(AgentSessionQueryMetadataKeys.SessionName));
    }

    [Fact]
    public void Label_AbsentFromBothDictionaries_ReturnsNull()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal),
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(record.Label(AgentSessionQueryMetadataKeys.SessionName));
        Assert.Null(record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
    }

    [Fact]
    public void Label_ProductionShape_RecordAndMetadataDictionariesCoincide()
    {
        // Mirrors AgentSessionQuery.ToRecords: the record's Labels dictionary
        // is the very same instance as session.Metadata.Labels. Both reads
        // must agree.
        var labels = SourceLabels();
        labels[AgentSessionQueryMetadataKeys.ProjectId] = "proj-coincide";
        var metadata = new AgentSessionMetadata(Labels: labels);
        var session = AgentSession.Create(
            id: "sess-coincide",
            runnerId: "runner-1",
            workDir: null,
            metadata: metadata,
            now: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var record = BuildRecord(session, metadata.Labels!);

        Assert.Equal("proj-coincide", record.Label(AgentSessionQueryMetadataKeys.ProjectId));
        Assert.Equal(metadata.Label(AgentSessionQueryMetadataKeys.ProjectId), record.Label(AgentSessionQueryMetadataKeys.ProjectId));
    }

    [Fact]
    public void IssueNumber_RecordLabelNumeric_ParsesInt()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.IssueNumber] = "42",
            },
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(42, record.IssueNumber());
    }

    [Fact]
    public void IssueNumber_MetadataFallbackNumeric_ParsesInt()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal),
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.IssueNumber] = "99",
            });

        Assert.Equal(99, record.IssueNumber());
    }

    [Fact]
    public void IssueNumber_RecordValueWinsOverMetadata()
    {
        var record = BuildRecord(
            recordLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.IssueNumber] = "10",
            },
            metadataLabels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.IssueNumber] = "20",
            });

        Assert.Equal(10, record.IssueNumber());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    [InlineData("12abc")]
    public void IssueNumber_AbsentOrNonNumeric_ReturnsZero(string? issueLabel)
    {
        var recordLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadataLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (issueLabel is not null)
            metadataLabels[AgentSessionQueryMetadataKeys.IssueNumber] = issueLabel;

        var record = BuildRecord(recordLabels, metadataLabels);

        Assert.Equal(0, record.IssueNumber());
    }

    private static AgentSessionRecord BuildRecord(
        IReadOnlyDictionary<string, string> recordLabels,
        IReadOnlyDictionary<string, string> metadataLabels)
    {
        var labels = SourceLabels();
        foreach (var (key, value) in metadataLabels)
            labels[key] = value;
        var metadata = new AgentSessionMetadata(Labels: labels, Annotations: null);
        var session = AgentSession.Create(
            id: "sess-under-test",
            runnerId: "runner-under-test",
            workDir: null,
            metadata: metadata,
            now: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        return BuildRecord(session, recordLabels);
    }

    private static AgentSessionRecord BuildRecord(
        AgentSession session,
        IReadOnlyDictionary<string, string> recordLabels)
    {
        var row = new AgentSessionRow
        {
            Id = session.Id,
            State = "{}",
            RunnerId = session.Runtime.RunnerId,
            Status = "opened",
            CreatedAt = session.Status.CreatedAt,
            AgentSessionId = (string?)null,
        };
        return new AgentSessionRecord(row, session, recordLabels);
    }

    private static Dictionary<string, string> SourceLabels() => new(StringComparer.Ordinal)
    {
        [AgentSessionQueryMetadataKeys.ProjectId] = "project-1",
        [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
        [GenericAgentSessionMetadata.AgentId] = "agent-1",
    };
}
