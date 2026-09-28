using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Domain;

[Trait("level", "L0")]
public sealed class WorkflowProvenanceTests
{
    [Fact]
    public void ProvenanceEvent_RoundTripsThroughCloudEventSerializer()
    {
        var original = new WorkflowProvenanceRecorded(
            WorkflowProvenanceActions.CiSkipped,
            WorkflowProvenanceActorKinds.System,
            "runner-1",
            "github-check-suite",
            "skipped",
            Stage: "check",
            TaskId: "verify.1",
            Attempt: 2,
            Reason: "conditional-workflow",
            Target: "Web");

        var data = WorkflowEventSerializer.ToData(original);
        var replayed = WorkflowEventSerializer.FromData(nameof(WorkflowProvenanceRecorded), data);
        var fact = Assert.IsType<WorkflowProvenanceRecorded>(WorkflowEventSerializer.Unwrap(replayed));

        Assert.Equal(original, fact);
        Assert.Equal(
            EventCatalog.ReverseDns.WorkflowProvenanceRecorded,
            WorkflowEventSerializer.BusType(original));
    }

    [Fact]
    public void StructuredPublicationOutputs_CreateFacts()
    {
        var push = Report(
            "mohist/push",
            new
            {
                kind = "push",
                status = "completed",
                pushed = true,
                remote = "origin",
                target = "mohist/ws-1",
                landedCommit = "abc123",
            });
        var pullRequest = Report(
            "mohist/create-github-pr",
            new
            {
                kind = "create-github-pr",
                status = "completed",
                operation = "created",
                prNumber = 1101,
                prUrl = "https://github.com/suraciii/mohist/pull/1101",
            });
        var merge = Report(
            "mohist/enable-github-pr-auto-merge",
            new
            {
                kind = "enable-github-pr-auto-merge",
                status = "completed",
                prNumber = 1101,
                prUrl = "https://github.com/suraciii/mohist/pull/1101",
                mergeCommitSha = "def456",
            });
        var ci = Report(
            "mohist/github-pr-checks",
            new
            {
                kind = "github-pr-checks",
                status = "verified",
                prNumber = 1101,
                checks = new[]
                {
                    new { name = "build", bucket = "pass" },
                    new { name = "optional", bucket = "skip" },
                },
            });

        Assert.Equal(WorkflowProvenanceActions.Push, Fact(push).Action);
        Assert.Equal(WorkflowProvenanceActions.PullRequest, Fact(pullRequest).Action);
        Assert.Equal("created", Fact(pullRequest).Outcome);
        Assert.Equal(WorkflowProvenanceActions.Merge, Fact(merge).Action);
        Assert.Equal("def456", Fact(merge).Result);
        var ciFacts = WorkflowProvenanceMapping.FromTaskReport(
            "stage", 1, "task.1", "mohist/github-pr-checks", "runner-1", ci);
        Assert.Collection(ciFacts,
            passed => { Assert.Equal(WorkflowProvenanceActions.CiPassed, passed.Action); Assert.Equal("build", passed.Result); },
            skipped => { Assert.Equal(WorkflowProvenanceActions.CiSkipped, skipped.Action); Assert.Equal("optional", skipped.Result); });
    }

    [Fact]
    public void MergedPrWithoutCommitSha_StillRecordsExternalEffect()
    {
        var report = Report("mohist/enable-github-pr-auto-merge", new
        {
            kind = "enable-github-pr-auto-merge",
            status = "completed",
            prNumber = 1101,
            prUrl = "https://github.com/suraciii/mohist/pull/1101",
        });

        var fact = Fact(report);
        Assert.Equal(WorkflowProvenanceActions.Merge, fact.Action);
        Assert.Equal("merged", fact.Outcome);
        Assert.Equal("https://github.com/suraciii/mohist/pull/1101", fact.Target);
    }

    [Fact]
    public void FreeTextAndAgentOutput_CannotCreateFacts()
    {
        var freeText = new TaskReport(
            "push.1",
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            Detail: "kind=push status=completed pushed=true");
        var agentOutput = Report(
            "mohist/agent",
            new { kind = "push", status = "completed", pushed = true });

        Assert.Empty(WorkflowProvenanceMapping.FromTaskReport(
            "build", 1, "push.1", "mohist/push", "runner-1", freeText));
        Assert.Empty(WorkflowProvenanceMapping.FromTaskReport(
            "build", 1, "agent.1", "mohist/agent", "runner-1", agentOutput));
    }

    [Fact]
    public void MergeVerification_RequiresTypedCheckEvidence()
    {
        var result = new CheckResult(
            "merge-verified",
            CheckResultStatus.Passed,
            Output: JsonSerializer.SerializeToElement(new
            {
                kind = "github-pr-status",
                status = "verified",
                prState = "MERGED",
                expectations = new[] { "merged" },
                prNumber = 1101,
                prUrl = "https://github.com/suraciii/mohist/pull/1101",
            }));

        var fact = Assert.Single(WorkflowProvenanceMapping.FromCheckResult(
            "integrate", 1, "mohist/github-pr-status", "runner-1", result));
        Assert.Equal(WorkflowProvenanceActions.Merge, fact.Action);
        Assert.Equal("verified", fact.Outcome);
    }

    private static TaskReport Report(string uses, object output) => new(
        "task.1",
        TaskReportStatus.Succeeded,
        JsonSerializer.SerializeToElement(output),
        Artifacts: null,
        ActionAttemptId: "task.1");

    private static WorkflowProvenanceRecorded Fact(TaskReport report) =>
        Assert.Single(WorkflowProvenanceMapping.FromTaskReport(
            "stage", 1, "task.1", UsesFor(report), "runner-1", report));

    private static string UsesFor(TaskReport report) =>
        report.Output!.Value.GetProperty("kind").GetString() switch
        {
            "push" => "mohist/push",
            "create-github-pr" => "mohist/create-github-pr",
            "enable-github-pr-auto-merge" => "mohist/enable-github-pr-auto-merge",
            "github-pr-checks" => "mohist/github-pr-checks",
            _ => throw new InvalidOperationException(),
        };
}
