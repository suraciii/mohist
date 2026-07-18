using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// issue-417 T-003: serialization round-trip coverage for the new
/// <see cref="IssueRepositoryChanged"/> event type. The bus type must
/// be the catalog-registered reverse-DNS string and the payload must
/// preserve every field after JSON round-trip.
/// </summary>
public class IssueEventSerializerTests
{
    [Fact]
    public void BusType_IssueRepositoryChanged_UsesCatalogReverseDns()
    {
        var payload = new IssueRepositoryChanged(
            OldRepositoryRef: "main",
            NewRepositoryRef: "web",
            CommandId: "cmd-1",
            ExpectedRevision: 5L,
            AppliedRevision: 6L);

        var busType = IssueEventSerializer.BusType(payload);

        Assert.Equal(EventCatalog.ReverseDns.IssueRepositoryChanged, busType);
        Assert.Equal("com.mohist.issue.repository-changed", busType);
    }

    [Fact]
    public void BusType_IssueRepositoryChanged_IsRegisteredInCatalog()
    {
        Assert.Contains(EventCatalog.ReverseDns.IssueRepositoryChanged, EventCatalog.All);
    }

    [Fact]
    public void ToData_IssueRepositoryChanged_RoundTripsPayload()
    {
        var payload = new IssueRepositoryChanged(
            OldRepositoryRef: "main",
            NewRepositoryRef: "web",
            CommandId: "cmd-1",
            ExpectedRevision: 5L,
            AppliedRevision: 6L);

        var data = IssueEventSerializer.ToData(payload);

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("main", data.GetProperty("oldRepositoryRef").GetString());
        Assert.Equal("web", data.GetProperty("newRepositoryRef").GetString());
        Assert.Equal("cmd-1", data.GetProperty("commandId").GetString());
        Assert.Equal(5L, data.GetProperty("expectedRevision").GetInt64());
        Assert.Equal(6L, data.GetProperty("appliedRevision").GetInt64());
    }

    [Fact]
    public void ToData_IssueRepositoryChanged_HandlesNullOldRepository()
    {
        // First-ever create-time repository assignment: OldRepositoryRef
        // is null. The serializer drops null fields (WhenWritingNull),
        // so we assert by property absence rather than JsonValueKind.Null.
        var payload = new IssueRepositoryChanged(
            OldRepositoryRef: null,
            NewRepositoryRef: "main",
            CommandId: "cmd-1",
            ExpectedRevision: null,
            AppliedRevision: 1L);

        var data = IssueEventSerializer.ToData(payload);

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.False(data.TryGetProperty("oldRepositoryRef", out _));
        Assert.False(data.TryGetProperty("expectedRevision", out _));
        Assert.Equal("main", data.GetProperty("newRepositoryRef").GetString());
        Assert.Equal("cmd-1", data.GetProperty("commandId").GetString());
        Assert.Equal(1L, data.GetProperty("appliedRevision").GetInt64());
    }

    [Fact]
    public void BusType_KnownEventTypes_ReturnExpectedReverseDnsStrings()
    {
        var cases = new (IssueEvent evt, string expectedType)[]
        {
            (new IssueCreated("T", "p2", new Dictionary<string, string>(), null, null), "com.mohist.issue.created"),
            (new IssueLabelsChanged(new Dictionary<string, string>(), new Dictionary<string, string>()), "com.mohist.issue.labels-changed"),
            (new IssuePriorityChanged("p2", "p1"), "com.mohist.issue.priority-changed"),
            (new IssueDraftChanged(false, true), "com.mohist.issue.draft-changed"),
            (new IssuePrerequisiteAdded(1), "com.mohist.issue.prerequisite-added"),
            (new IssuePrerequisiteRemoved(1), "com.mohist.issue.prerequisite-removed"),
            (new IssueWorkflowProfileChanged(null), "com.mohist.issue.workflow-profile-changed"),
            (new IssueWorkStarted("wr_1"), "com.mohist.issue.work-started"),
            (new IssueCompleted("wr_1"), EventCatalog.ReverseDns.IssueCompleted),
            (new IssueCancelled(null), EventCatalog.ReverseDns.IssueCancelled),
            (new IssueArchived(), "com.mohist.issue.archived"),
            (new IssueUnarchived(), "com.mohist.issue.unarchived"),
            (new IssueReopened(), "com.mohist.issue.reopened"),
            (new IssueRepositoryChanged("main", "web", "cmd-1", 1L, 2L), "com.mohist.issue.repository-changed"),
        };

        foreach (var (evt, expected) in cases)
        {
            Assert.Equal(expected, IssueEventSerializer.BusType(evt));
        }
    }
}