using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public class EventCatalogTests
{
    [Fact]
    public void All_ContainsEveryReverseDnsConstant()
    {
        foreach (var field in typeof(EventCatalog.ReverseDns).GetFields())
        {
            if (field.GetRawConstantValue() is string value)
            {
                Assert.Contains(value, EventCatalog.All);
            }
        }
    }

    [Fact]
    public void All_HasExactlyOneLineageDeclarationPerProtocolType()
    {
        Assert.All(EventCatalog.All, type => Assert.True(EventCatalog.HasLineageDeclaration(type), type));
        Assert.DoesNotContain(EventCatalog.All, type => EventCatalog.RequiredAttributes(type).Count == 0);
    }

    [Fact]
    public void ProducedTypes_CoverEveryNonCatalogOnlyProtocolType()
    {
        var produced = WorkflowEventSerializer.ProducedTypes
            .Concat(IssueEventSerializer.ProducedTypes)
            .Concat(AgentSessionEventSerializer.ProducedTypes)
            .Concat(EpicEventSerializer.ProducedTypes)
            .Append(EventCatalog.ReverseDns.InboxItemPersisted)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = EventCatalog.All
            .Where(type => !EventCatalog.CatalogOnlyTypes.Contains(type))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, produced);
    }

    [Fact]
    public void ValidateDeclarations_RegisteredTypeWithoutDeclaration_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => EventCatalog.ValidateDeclarations(
            ["com.mohist.example.created"],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));

        Assert.Contains("Undeclared: com.mohist.example.created", ex.Message);
    }

    [Fact]
    public void RequiredAttributes_WorkflowRunTypes_CarryProjectIdAndWorkflowRunIdOnly()
    {
        var expected = new[] { EventCatalog.Lineage.ProjectId, EventCatalog.Lineage.WorkflowRunId };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.WorkflowRunStarted).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.WorkflowRunCompleted).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.WorkflowRunFailed).ToArray());
    }

    [Fact]
    public void RequiredAttributes_StageTaskCheckFeedback_TypesCarryStageOnTopOfWorkflowBase()
    {
        var expected = new[]
        {
            EventCatalog.Lineage.ProjectId,
            EventCatalog.Lineage.WorkflowRunId,
            EventCatalog.Lineage.Stage,
        };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.StageStarted).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.StageApprovalRequested).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.FeedbackRequested).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.TaskCompleted).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.CheckPassed).ToArray());
    }

    [Fact]
    public void RequiredAttributes_IssueTypes_CarryProjectIdAndIssueNumber()
    {
        var expected = new[]
        {
            EventCatalog.Lineage.ProjectId,
            EventCatalog.Lineage.Issue,
        };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.IssueCreated).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.IssueCompleted).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.IssueArchived).ToArray());
    }

    [Fact]
    public void RequiredAttributes_EpicTypes_CarryProjectIdAndEpicNumber()
    {
        var expected = new[] { EventCatalog.Lineage.ProjectId, EventCatalog.Lineage.Epic };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.EpicCreated).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.EpicIssueLinked).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.EpicClosed).ToArray());
    }

    [Fact]
    public void RequiredAttributes_AgentSessionTypes_CarryProjectIdAndSessionId()
    {
        var expected = new[] { EventCatalog.Lineage.ProjectId, EventCatalog.Lineage.SessionId };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.AgentSessionRuntimeBound).ToArray());
        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.AgentSessionUsageRecorded).ToArray());
    }

    [Fact]
    public void RequiredAttributes_RunnerTypes_CarryRunnerIdOnly()
    {
        var expected = new[] { EventCatalog.Lineage.RunnerId };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.RunnerDisconnected).ToArray());
    }

    [Fact]
    public void RequiredAttributes_InboxItemPersisted_CarriesProjectIdAndIssueNumber()
    {
        var expected = new[]
        {
            EventCatalog.Lineage.ProjectId,
            EventCatalog.Lineage.Issue,
        };

        Assert.Equal(expected, EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.InboxItemPersisted).ToArray());
    }

    [Fact]
    public void RequiredAttributes_CatalogOnlyRepairScheduled_DeclaresWorkflowBaseDespiteNoProducer()
    {
        Assert.True(EventCatalog.HasLineageDeclaration(EventCatalog.ReverseDns.RepairScheduled));
        Assert.Equal(
            new[] { EventCatalog.Lineage.ProjectId, EventCatalog.Lineage.WorkflowRunId },
            EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.RepairScheduled).ToArray());
    }

    [Fact]
    public void UnproducibleWorkflowCheckStarted_IsNotCataloged()
    {
        const string type = "com.mohist.workflow.check.started";

        Assert.DoesNotContain(type, EventCatalog.All);
        Assert.DoesNotContain(type, EventCatalog.CatalogOnlyTypes);
        Assert.False(EventCatalog.HasLineageDeclaration(type));
        Assert.Empty(EventCatalog.RequiredAttributes(type));
    }

    [Fact]
    public void RequiredAttributes_CatalogOnlyRunnerDisconnected_DeclaresRunnerIdBaseDespiteNoProducer()
    {
        Assert.True(EventCatalog.HasLineageDeclaration(EventCatalog.ReverseDns.RunnerDisconnected));
    }

    [Fact]
    public void RequiredAttributes_WorkflowArtifactRecorded_IsNotStageBearing()
    {
        var required = EventCatalog.RequiredAttributes(EventCatalog.ReverseDns.WorkflowArtifactRecorded);

        Assert.DoesNotContain(EventCatalog.Lineage.Stage, required);
        Assert.Contains(EventCatalog.Lineage.ProjectId, required);
        Assert.Contains(EventCatalog.Lineage.WorkflowRunId, required);
    }

    [Fact]
    public void RequiredAttributes_UnknownType_ReturnsEmpty()
    {
        Assert.Empty(EventCatalog.RequiredAttributes("com.example.unknown"));
    }

    [Fact]
    public void HasLineageDeclaration_TranscriptTypes_ReturnsFalse()
    {
        Assert.False(EventCatalog.HasLineageDeclaration("session.input"));
        Assert.False(EventCatalog.HasLineageDeclaration("message.delta"));
    }
}

public class EnvelopeConformanceTests
{
    private static CloudEvent NewEnvelope(string type, params (string Key, string Value)[] entries)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            extensions[key] = value;
        }

        return new CloudEvent(
            id: "evt_1",
            source: new Uri("/mohist/projects/proj_1/issues/42", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            data: JsonDocument.Parse("{}").RootElement,
            extensions: extensions);
    }

    [Fact]
    public void AssertRequired_EnvelopeWithAllRequired_Passes()
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.WorkflowRunStarted,
            (EventCatalog.Lineage.ProjectId, "proj_1"),
            (EventCatalog.Lineage.WorkflowRunId, "wr_1"));

        EnvelopeConformance.AssertRequired(envelope);
    }

    [Fact]
    public void AssertRequired_EnvelopeWithAllRequiredPlusExtra_Passes()
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.IssueCreated,
            (EventCatalog.Lineage.ProjectId, "proj_1"),
            (EventCatalog.Lineage.Issue, "42"),
            (EventCatalog.Lineage.Epic, "7"));

        EnvelopeConformance.AssertRequired(envelope);
    }

    [Fact]
    public void AssertRequired_StageEnvelopeMissingStage_FailsWithStageAttribute()
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.StageStarted,
            (EventCatalog.Lineage.ProjectId, "proj_1"),
            (EventCatalog.Lineage.WorkflowRunId, "wr_1"));

        var ex = Assert.Throws<EnvelopeConformanceException>(() => EnvelopeConformance.AssertRequired(envelope));

        Assert.Equal(EventCatalog.ReverseDns.StageStarted, ex.EventType);
        Assert.Contains(EventCatalog.Lineage.Stage, ex.MissingAttributes);
        Assert.DoesNotContain(EventCatalog.Lineage.ProjectId, ex.MissingAttributes);
        Assert.DoesNotContain(EventCatalog.Lineage.WorkflowRunId, ex.MissingAttributes);
    }

    [Fact]
    public void AssertRequired_WorkflowRunEnvelopeMissingRequired_Fails()
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.WorkflowRunStarted,
            (EventCatalog.Lineage.ProjectId, "proj_1"));

        var ex = Assert.Throws<EnvelopeConformanceException>(() => EnvelopeConformance.AssertRequired(envelope));

        Assert.Equal(EventCatalog.ReverseDns.WorkflowRunStarted, ex.EventType);
        Assert.Contains(EventCatalog.Lineage.WorkflowRunId, ex.MissingAttributes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AssertRequired_EnvelopeWithBlankValueForRequired_Fails(string projectId)
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.WorkflowRunStarted,
            (EventCatalog.Lineage.ProjectId, projectId),
            (EventCatalog.Lineage.WorkflowRunId, "wr_1"));

        var ex = Assert.Throws<EnvelopeConformanceException>(() => EnvelopeConformance.AssertRequired(envelope));

        Assert.Contains(EventCatalog.Lineage.ProjectId, ex.MissingAttributes);
    }

    [Fact]
    public void AssertRequired_ExtensionsOverload_StageEnvelopeMissingStage_Fails()
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = "proj_1",
            [EventCatalog.Lineage.WorkflowRunId] = "wr_1",
        };

        var ex = Assert.Throws<EnvelopeConformanceException>(() =>
            EnvelopeConformance.AssertRequired(extensions, EventCatalog.ReverseDns.TaskCompleted));

        Assert.Contains(EventCatalog.Lineage.Stage, ex.MissingAttributes);
    }

    [Fact]
    public void AssertRequired_UnknownTypeIsNoOp()
    {
        var envelope = NewEnvelope("com.example.unknown");

        EnvelopeConformance.AssertRequired(envelope);
    }

    [Fact]
    public void Missing_StageEnvelopeMissingStage_ReturnsStageOnly()
    {
        var envelope = NewEnvelope(
            EventCatalog.ReverseDns.FeedbackRequested,
            (EventCatalog.Lineage.ProjectId, "proj_1"),
            (EventCatalog.Lineage.WorkflowRunId, "wr_1"));

        var missing = EnvelopeConformance.Missing(envelope);

        Assert.Equal(new[] { EventCatalog.Lineage.Stage }, missing);
    }
}
