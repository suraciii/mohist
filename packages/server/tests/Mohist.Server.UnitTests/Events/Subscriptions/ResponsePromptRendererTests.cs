using System.Text.Json;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Xunit;

namespace Mohist.Server.UnitTests.Events.Subscriptions;

/// <summary>
/// Unit specs for <see cref="ResponsePromptRenderer"/> (issue-391 T-003).
/// The renderer does plain string substitution against three envelope
/// fields — no template engine, no <c>{{issue}}</c> variable. Spec
/// <c>agent-subscription-dispatch#Response prompt is rendered from
/// envelope-carried variables</c> drives the assertions.
/// </summary>
public class ResponsePromptRendererTests
{
    [Fact]
    public void Render_EventAttributesSubstitutePresentEmptyAndLeaveAbsentVerbatim()
    {
        var input = new DictionaryInput(
            new Dictionary<string, string?>
            {
                ["issue"] = "42",
                ["empty"] = string.Empty,
            });

        var rendered = ResponsePromptRenderer.Render(
            "{{event.issue}}|{{event.empty}}|{{event.missing}}",
            input);

        Assert.Equal("42||{{event.missing}}", rendered);
    }

    [Fact]
    public void Render_EventInputKeepsLegacyAliases()
    {
        var input = new DictionaryInput(new Dictionary<string, string?>
        {
            ["workflowrunid"] = "wr-1",
            ["stage"] = "plan",
            ["type"] = "event.type",
        });

        Assert.Equal("wr-1|plan|event.type", ResponsePromptRenderer.Render(
            "{{workflow_run_id}}|{{stage}}|{{event_type}}", input));
    }
    [Fact]
    public void Render_WorkflowEventWithSourceAndStageData_SubstitutesAllThreeVariables()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_abc",
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            stage: "plan");
        var template = "Workflow {{workflow_run_id}} stage={{stage}} event={{event_type}}";

        var rendered = ResponsePromptRenderer.Render(template, evt);

        Assert.Equal("Workflow wr_abc stage=plan event=com.mohist.workflow.stage.approval-requested", rendered);
    }

    [Fact]
    public void Render_WorkflowEventDataWithValueEnvelope_StillExtractsStage()
    {
        // WorkflowEvent is a C# 14 union, serialized as {"value":{"stage":"build"}}
        // when wrapped by the bus path. The renderer must unwrap this envelope
        // exactly like WorkflowStageLockReleaseHandler.ExtractStage does.
        var data = JsonSerializer.SerializeToElement(new
        {
            value = new { stage = "build" },
        });
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_xyz",
            type: EventCatalog.ReverseDns.StageCompleted,
            stage: "build",
            data: data);
        var template = "stage={{stage}}";

        var rendered = ResponsePromptRenderer.Render(template, evt);

        Assert.Equal("stage=build", rendered);
    }

    [Fact]
    public void Render_DataWithBareStageKey_SubstitutesStage()
    {
        var data = JsonSerializer.SerializeToElement(new { stage = "check" });
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_uvw",
            type: "com.mohist.workflow.stage.started",
            stage: "check",
            data: data);
        var template = "stage={{stage}}";

        Assert.Equal("stage=check", ResponsePromptRenderer.Render(template, evt));
    }

    [Fact]
    public void Render_EnvelopeStageWinsWithoutChangingPayload()
    {
        var data = JsonSerializer.SerializeToElement(new { stage = "payload-stage" });
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_context",
            type: EventCatalog.ReverseDns.StageStarted,
            stage: "envelope-stage",
            data: data);

        Assert.Equal("stage=envelope-stage", ResponsePromptRenderer.Render("stage={{stage}}", evt));
        Assert.Equal("payload-stage", data.GetProperty("stage").GetString());
    }

    [Fact]
    public void Render_NoWorkflowSource_LeavesWorkflowRunIdTokenEmpty()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/something/else",
            type: "com.mohist.something.happened");
        var template = "run={{workflow_run_id}}";

        Assert.Equal("run=", ResponsePromptRenderer.Render(template, evt));
    }

    [Fact]
    public void Render_NoStageOnEvent_LeavesStageTokenEmpty()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_1",
            type: "com.mohist.workflow.run.failed",
            data: null);
        var template = "stage={{stage}}";

        Assert.Equal("stage=", ResponsePromptRenderer.Render(template, evt));
    }

    [Fact]
    public void Render_UnknownPlaceholder_LeftVerbatimAndDoesNotThrow()
    {
        // Spec scenario "Unsubstituted placeholders left as-is when no
        // envelope value" — unmatched tokens must remain so users can
        // detect typos / unsupported variable names.
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_42",
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            stage: "plan");
        var template = "{{workflow_run_id}} | {{unknown_token}} | {{stage}}";

        var rendered = ResponsePromptRenderer.Render(template, evt);

        Assert.Equal("wr_42 | {{unknown_token}} | plan", rendered);
    }

    [Fact]
    public void Render_DoesNotProvideIssuePlaceholder_NoSubstitutionOccursForIssueToken()
    {
        // Spec explicitly forbids an {{issue}} variable — Agent obtains
        // issue identity itself by running mo workflow get.
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_issue_test",
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            stage: "plan");
        var template = "issue={{issue}} run={{workflow_run_id}}";

        var rendered = ResponsePromptRenderer.Render(template, evt);

        Assert.Equal("issue={{issue}} run=wr_issue_test", rendered);
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_x",
            type: "com.mohist.workflow.run.failed");

        Assert.Equal(string.Empty, ResponsePromptRenderer.Render(string.Empty, evt));
        Assert.Equal(string.Empty, ResponsePromptRenderer.Render(null, evt));
    }

    [Fact]
    public void Render_NullEvent_ReturnsTemplateVerbatim()
    {
        var template = "literal text with {{workflow_run_id}}";

        Assert.Equal(template, ResponsePromptRenderer.Render(template, (CloudEvent?)null));
    }

    [Fact]
    public void Render_NoMatchingTokens_ReturnsTemplateUnchanged()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_x",
            type: "com.mohist.workflow.run.failed");
        var template = "static prose with no placeholders";

        Assert.Equal(template, ResponsePromptRenderer.Render(template, evt));
    }

    [Fact]
    public void Render_MultipleOccurrencesOfSameToken_AreAllReplaced()
    {
        var evt = BuildWorkflowEvent(
            source: "/mohist/workflow-runs/wr_dup",
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            stage: "plan");
        var template = "{{workflow_run_id}} and again {{workflow_run_id}}";

        Assert.Equal(
            "wr_dup and again wr_dup",
            ResponsePromptRenderer.Render(template, evt));
    }

    private static CloudEvent BuildWorkflowEvent(
        string source,
        string type,
        string? stage = null,
        JsonElement? data = null)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        const string prefix = "/mohist/workflow-runs/";
        if (source.StartsWith(prefix, StringComparison.Ordinal))
            extensions[EventCatalog.Lineage.WorkflowRunId] = source[prefix.Length..];
        if (!string.IsNullOrWhiteSpace(stage))
            extensions[EventCatalog.Lineage.Stage] = stage;
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: data,
            extensions: extensions);
    }

    private sealed class DictionaryInput(IReadOnlyDictionary<string, string?> values) : EventMatchInput
    {
        public string GetValue(string attribute) => values.GetValueOrDefault(attribute) ?? string.Empty;

        public bool Has(string attribute) => values.ContainsKey(attribute);
    }
}
