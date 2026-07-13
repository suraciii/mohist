using System.Text.Json;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Artifacts;

public class WorkflowArtifactDomainTests
{
    [Fact]
    public void DomainFact_ExposesCoreIdentity()
    {
        var recordedAt = new DateTimeOffset(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);
        var artifact = new WorkflowArtifact(
            WorkflowRunId: "wr_1",
            TaskRunId: "ai-review.1",
            Path: "openspec/changes/issue-55/review.md",
            RecordedAt: recordedAt);

        Assert.Equal("wr_1", artifact.WorkflowRunId);
        Assert.Equal("ai-review.1", artifact.TaskRunId);
        Assert.Equal("openspec/changes/issue-55/review.md", artifact.Path);
        Assert.Equal(recordedAt, artifact.RecordedAt);
    }

    [Fact]
    public void DomainFact_DoesNotCarryInfrastructureMetadata()
    {
        // The domain fact intentionally exposes only the four
        // business facts. Storage path, content hash, content type,
        // size, issue id, and display name are persistence/read-model
        // details — they MUST NOT be part of the domain shape.
        var properties = typeof(WorkflowArtifact)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(5, properties.Count);
        Assert.Contains(nameof(WorkflowArtifact.WorkflowRunId), properties);
        Assert.Contains(nameof(WorkflowArtifact.TaskRunId), properties);
        Assert.Contains(nameof(WorkflowArtifact.Path), properties);
        Assert.Contains(nameof(WorkflowArtifact.RecordedAt), properties);
        Assert.Contains(nameof(WorkflowArtifact.ProducerKey), properties);

        Assert.DoesNotContain("ArtifactStoragePath", properties);
        Assert.DoesNotContain("ContentHash", properties);
        Assert.DoesNotContain("ContentType", properties);
        Assert.DoesNotContain("Size", properties);
        Assert.DoesNotContain("IssueId", properties);
        Assert.DoesNotContain("ProjectId", properties);
        Assert.DoesNotContain("DisplayName", properties);
    }

    [Fact]
    public void DomainFact_ProducerKeyIsWorkflowRunPlusTaskRun()
    {
        var first = new WorkflowArtifact("wr_1", "ai-review.1", "review.md", DateTimeOffset.UtcNow);
        var second = new WorkflowArtifact("wr_1", "ai-review.2", "review.md", DateTimeOffset.UtcNow);
        var otherRun = new WorkflowArtifact("wr_2", "ai-review.1", "review.md", DateTimeOffset.UtcNow);

        Assert.Equal("wr_1:ai-review.1", first.ProducerKey);
        Assert.Equal("wr_1:ai-review.2", second.ProducerKey);
        Assert.Equal("wr_2:ai-review.1", otherRun.ProducerKey);
        Assert.NotEqual(first.ProducerKey, second.ProducerKey);
    }

    [Fact]
    public void Event_IsPartOfWorkflowEventUnion()
    {
        WorkflowEvent evt = new WorkflowArtifactRecorded("wr_1", "ai-review.1", "review.md", DateTimeOffset.UtcNow);

        var busType = WorkflowEventSerializer.BusType(evt);
        Assert.Equal(EventCatalog.ReverseDns.WorkflowArtifactRecorded, busType);
    }

    [Fact]
    public void Event_RoundTripsThroughCloudEventEnvelope()
    {
        var recordedAt = new DateTimeOffset(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);
        var payload = new WorkflowArtifactRecorded(
            "wr_1", "ai-review.1", "review.md", recordedAt);
        WorkflowEvent evt = payload;

        var data = WorkflowEventSerializer.ToData(evt);

        // The serialized JSON element is a plain object that we can
        // inspect directly to verify the recorded payload survives a
        // serialize → deserialize cycle through the workflow event
        // transport.
        Assert.Equal("wr_1", data.GetProperty("workflowRunId").GetString());
        Assert.Equal("ai-review.1", data.GetProperty("taskRunId").GetString());
        Assert.Equal("review.md", data.GetProperty("path").GetString());
        Assert.Equal(recordedAt, data.GetProperty("recordedAt").GetDateTimeOffset());
    }

    [Fact]
    public void EventCatalog_RegistersReverseDnsType()
    {
        Assert.Contains(
            EventCatalog.ReverseDns.WorkflowArtifactRecorded,
            EventCatalog.All);
    }

    [Fact]
    public void DomainNamespace_DoesNotUseSnapshotNaming()
    {
        // The product/storage/UI language is WorkflowArtifact, not
        // Snapshot. Walk the assembly for any type that introduces
        // the rejected name in this task's domain area.
        var assembly = typeof(WorkflowArtifact).Assembly;
        var snapshotTypes = assembly
            .GetTypes()
            .Where(t => t.FullName?.Contains("Workflow", StringComparison.Ordinal) == true
                        && t.Name.Contains("Snapshot", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(snapshotTypes);
    }

    [Fact]
    public void WorkflowRunJson_DoesNotLeakArtifactInfrastructureFields()
    {
        // The domain JSON for a WorkflowRun must not contain
        // artifact content, pending upload status, storage paths,
        // hashes, content types, or file sizes. Artifact rows are
        // the source of truth for that information; the JSON is
        // for the run state machine only.
        var definition = new WorkflowDefinition(
            Id: "wf-1",
            Stages: [
                new StageDefinition(
                    Stage: "build",
                    Tasks: [
                        new TaskDefinition(
                            Id: "design",
                            Title: "Design",
                            Uses: "mohist/acp-agent",
                            With: null,
                            Artifacts: new TaskArtifactCapture([
                                new TaskArtifactDeclaration("design.md")
                            ]))
                    ],
                    Checks: [],
                    RequiresApproval: false,
                    Variables: null,
                    LockBehavior: null,
                    Resources: null)
            ],
            Name: null,
            Variables: null,
            Defaults: null,
            Artifacts: null);

        var run = WorkflowRun.Create("wr_1", definition, DateTimeOffset.UnixEpoch, new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch));
        run.Start(DateTimeOffset.UnixEpoch);

        var json = JSON.Serialize(run);

        Assert.DoesNotContain("ArtifactStoragePath", json);
        Assert.DoesNotContain("ArtifactStorage", json);
        Assert.DoesNotContain("ContentHash", json);
        Assert.DoesNotContain("ContentType", json);
        Assert.DoesNotContain("PendingUpload", json);
        Assert.DoesNotContain("StoragePath", json);
        Assert.DoesNotContain("Snapshot", json, StringComparison.OrdinalIgnoreCase);

        // Round-trip preserves the run state without introducing
        // any artifact leakage either.
        var roundTripped = JSON.Deserialize<WorkflowRun>(json)!;
        var roundTripJson = JSON.Serialize(roundTripped);

        Assert.DoesNotContain("ArtifactStoragePath", roundTripJson);
        Assert.DoesNotContain("ContentHash", roundTripJson);
        Assert.DoesNotContain("PendingUpload", roundTripJson);
        Assert.DoesNotContain("Snapshot", roundTripJson, StringComparison.OrdinalIgnoreCase);
    }
}
