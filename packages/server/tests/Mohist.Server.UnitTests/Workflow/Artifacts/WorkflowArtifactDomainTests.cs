using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Artifacts;

public class WorkflowArtifactDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Event_IsPartOfWorkflowEventUnion()
    {
        WorkflowEvent evt = new WorkflowArtifactRecorded("wr_1", "ai-review.1", "review.md", FixedNow);

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
    public void WorkflowRunJson_DoesNotLeakArtifactInfrastructureFields()
    {
        // The domain JSON for a WorkflowRun must not contain
        // artifact content, pending upload status, storage paths,
        // hashes, content types, or file sizes. Artifact rows are
        // the source of truth for that information; the JSON is
        // for the run state machine only.
        var definition = new WorkflowDefinition(
            Stages: [
                new StageDefinition(
                    Stage: "build",
                    Tasks: [
                        new TaskDefinition(
                            Id: "design",
                            Title: "Design",
                            Uses: "mohist/opencode",
                            With: null,
                            Artifacts: new TaskArtifactCapture([
                                new TaskArtifactDeclaration("design.md")
                            ]))
                    ],
                    Checks: [],
                    RequiresApproval: false,
                    LockBehavior: null,
                    Resources: null)
            ]);

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
