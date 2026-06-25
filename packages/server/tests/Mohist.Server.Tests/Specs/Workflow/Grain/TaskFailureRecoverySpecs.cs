using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class TaskFailureRecoverySpecs : WorkflowGrainSpecs
{
    public TaskFailureRecoverySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_ParsesTaskFailureRetrySelf()
    {
        var yaml = """
        stages:
          - stage: integrate
            tasks:
              - id: merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                onFailure:
                  limit: 2
                  cases:
                    - when:
                        output.errorCode: base-moved
                      retry: self
                      tasks:
                        - id: recover:rebase
                          title: Rebase after base moved
                          uses: mohist/rebase
            checks: []
        """;

        var definition = WorkflowYamlSerializer.FromYaml(yaml);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.OnFailure);
        Assert.Equal(2, task.OnFailure!.Limit);
        var failureCase = Assert.Single(task.OnFailure.Cases);
        Assert.True(failureCase.RetrySelf);
        Assert.Equal("base-moved", failureCase.When["output.errorCode"]!.Value.GetString());
        Assert.Equal(new[] { "recover:rebase" }, failureCase.Tasks.Select(t => t.Id).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_RoundTripsTaskFailureRetrySelf()
    {
        var yaml = """
        stages:
          - stage: integrate
            tasks:
              - id: merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                onFailure:
                  limit: 2
                  cases:
                    - when:
                        output.errorCode: base-moved
                      retry: self
                      tasks:
                        - id: recover:rebase
                          title: Rebase after base moved
                          uses: mohist/rebase
            checks: []
        """;

        var definition = WorkflowYamlSerializer.FromYaml(yaml);
        var emitted = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(emitted);

        var task = reparsed.Stages.Single().Tasks.Single();
        var failureCase = Assert.Single(task.OnFailure!.Cases);
        Assert.True(failureCase.RetrySelf);
        Assert.Contains("retry: self", emitted);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_RejectsOnFailureOnRecoveryTaskDepth()
    {
        // A recovery task (declared under an onFailure case) is permitted
        // to declare its own onFailure (depth 1). A task declared under
        // that nested onFailure is a recovery-of-recovery (depth 2) and
        // MUST be rejected at parse time so the runtime never has to
        // recurse beyond depth 1.
        var yaml = """
        stages:
          - stage: integrate
            tasks:
              - id: merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                onFailure:
                  limit: 2
                  cases:
                    - when:
                        output.errorCode: base-moved
                      tasks:
                        - id: recover:rebase
                          title: Rebase after base moved
                          uses: mohist/rebase
                          onFailure:
                            limit: 1
                            cases:
                              - when:
                                  output.failureKind: conflict
                                tasks:
                                  - id: recover:resolve-rebase-conflicts
                                    title: Resolve rebase conflicts
                                    uses: mohist/acp-agent
                                    onFailure:
                                      limit: 1
                                      cases:
                                        - when:
                                            output.errorCode: still-conflict
                                          tasks:
                                            - id: recover:give-up
                                              title: Give up
                                              uses: mohist/acp-agent
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("recover:resolve-rebase-conflicts", ex.Message);
        Assert.Contains("must not declare its own onFailure", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_AllowsNestedOnFailureOnRecoveryTaskDepthOne()
    {
        // A recovery task MAY declare its own onFailure (depth 1).
        // Recovery tasks under that nested onFailure are not allowed,
        // but the depth-1 declaration itself parses successfully.
        var yaml = """
        stages:
          - stage: integrate
            tasks:
              - id: merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                onFailure:
                  limit: 2
                  cases:
                    - when:
                        output.errorCode: base-moved
                      tasks:
                        - id: recover:rebase
                          title: Rebase after base moved
                          uses: mohist/rebase
                          onFailure:
                            limit: 1
                            cases:
                              - when:
                                  output.failureKind: conflict
                                tasks:
                                  - id: recover:resolve-rebase-conflicts
                                    title: Resolve rebase conflicts
                                    uses: mohist/acp-agent
            checks: []
        """;

        var definition = WorkflowYamlSerializer.FromYaml(yaml);

        var task = definition.Stages.Single().Tasks.Single();
        var failureCase = Assert.Single(task.OnFailure!.Cases);
        var rebase = Assert.Single(failureCase.Tasks);
        Assert.Equal("recover:rebase", rebase.Id);
        Assert.NotNull(rebase.OnFailure);
        var nestedCase = Assert.Single(rebase.OnFailure!.Cases);
        Assert.Equal(new[] { "recover:resolve-rebase-conflicts" }, nestedCase.Tasks.Select(t => t.Id).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_RejectsInvalidRetryValue()
    {
        var yaml = """
        stages:
          - stage: integrate
            tasks:
              - id: merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                onFailure:
                  limit: 1
                  cases:
                    - when:
                        output.errorCode: base-moved
                      retry: bogus
                      tasks:
                        - id: recover:rebase
                          title: Rebase
                          uses: mohist/rebase
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("retry", ex.Message);
        Assert.Contains("self", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FailedTask_WithRetrySelf_AppendsFreshAttemptAfterRecoveryTasks()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "integrate:merge-pr",
                    "Merge GitHub PR",
                    "mohist/merge-github-pr",
                    OnFailure: new TaskFailureAction(
                        2,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("base-moved")
                                },
                                [
                                    new TaskDefinition("recover:rebase", "Rebase", "mohist/rebase"),
                                ],
                                RetrySelf: true)
                        ]))
            ],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (mergePr, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("integrate:merge-pr.", mergePr.WorkId);
        await ReportAsync(r1, mergePr.WorkId, new WorkResult("failed", "base moved", Output: """
        {
          "errorCode": "base-moved"
        }
        """));

        // First inserted: the recovery task.
        var (rebase, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.", rebase.WorkId);
        Assert.Equal("mohist/rebase", rebase.Uses);
        await ReportAsync(r2, rebase.WorkId, "completed");

        // Then the fresh retry of the original task.
        var (retry, r3) = await PollWorkAnyAsync();
        Assert.Equal("integrate:merge-pr.2", retry.WorkId);
        Assert.Equal("mohist/merge-github-pr", retry.Uses);

        // The retried attempt preserves the original task's onFailure:
        // completing it and proceeding to checks confirms the cloned
        // TaskDefinition still resolves to the same retry sequence.
        await ReportAsync(r3, retry.WorkId, "completed");
        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RetriedTask_FailsAgain_EvaluatesOriginalOnFailureAndExhaustsAtLimit()
    {
        // Limit=2: first failure → recovery+retry; second failure →
        // recovery+retry again. The third failure exhausts the limit and
        // remains an ordinary task failure with no further recovery.
        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "integrate:merge-pr",
                    "Merge GitHub PR",
                    "mohist/merge-github-pr",
                    OnFailure: new TaskFailureAction(
                        2,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("base-moved")
                                },
                                [
                                    new TaskDefinition("recover:rebase", "Rebase", "mohist/rebase"),
                                ],
                                RetrySelf: true)
                        ]))
            ],
            checks: []));

        // First failure → recovery (recover:rebase.1) + retry (merge-pr.2)
        var (mergePr1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, mergePr1.WorkId, new WorkResult("failed", "base moved", Output: """
        {
          "errorCode": "base-moved"
        }
        """));
        var (rebase1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.", rebase1.WorkId);
        await ReportAsync(r2, rebase1.WorkId, "completed");
        var (mergePr2, r3) = await PollWorkAnyAsync();
        Assert.Equal("integrate:merge-pr.2", mergePr2.WorkId);

        // Second failure: original onFailure re-evaluates against the
        // retry's output and matches again. failedAttempts = 1
        // (integrate:merge-pr.1), Limit = 2, so this second recovery
        // attempt is still allowed and must append a fresh retry.
        await ReportAsync(r3, mergePr2.WorkId, new WorkResult("failed", "base moved again", Output: """
        {
          "errorCode": "base-moved"
        }
        """));
        var (rebase2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.", rebase2.WorkId);
        await ReportAsync(r4, rebase2.WorkId, "completed");

        var (mergePr3, r5) = await PollWorkAnyAsync();
        Assert.Equal("integrate:merge-pr.3", mergePr3.WorkId);

        // Third failure: failedAttempts = 2 meets Limit = 2, so the
        // engine preserves the failure and injects no more recovery.
        await ReportAsync(r5, mergePr3.WorkId, new WorkResult("failed", "base moved after retries", Output: """
        {
          "errorCode": "base-moved"
        }
        """));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetRunStatusAsync();
        Assert.Equal("Failed", status);

        var run = await LoadRunAsync(_workflowId!);
        var mergePrFailures = run.Stages.Single().Tasks
            .Where(t => t.DefinitionId == "integrate:merge-pr")
            .ToList();
        Assert.Equal(3, mergePrFailures.Count);
        Assert.All(mergePrFailures, t => Assert.Equal(TaskRunStatus.Failed, t.Status));

        var rebaseRecoveries = run.Stages.Single().Tasks
            .Where(t => t.DefinitionId == "recover:rebase")
            .ToList();
        Assert.Equal(2, rebaseRecoveries.Count);
        Assert.All(rebaseRecoveries, t => Assert.Equal(TaskRunStatus.Completed, t.Status));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RecoveryTask_WithOwnOnFailure_TriggersNestedRecovery()
    {
        // Rebase-conflict canonical shape:
        //   integrate:merge-pr (Limit=2)
        //     onFailure base-moved → recover:rebase (has own onFailure),
        //                            recover:push
        //   recover:rebase (Limit=1) onFailure failureKind:conflict →
        //                            recover:resolve-rebase-conflicts
        // The nested recovery path is depth 1 — the parser accepts it,
        // the runtime matches it when the recovery task fails.
        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "integrate:merge-pr",
                    "Merge GitHub PR",
                    "mohist/merge-github-pr",
                    OnFailure: new TaskFailureAction(
                        2,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("base-moved")
                                },
                                [
                                    new TaskDefinition(
                                        "recover:rebase",
                                        "Rebase",
                                        "mohist/rebase",
                                        OnFailure: new TaskFailureAction(
                                            1,
                                            [
                                                new TaskFailureCase(
                                                    new Dictionary<string, JsonElement?>
                                                    {
                                                        ["output.failureKind"] = JsonSerializer.SerializeToElement("conflict")
                                                    },
                                                    [
                                                        new TaskDefinition(
                                                            "recover:resolve-rebase-conflicts",
                                                            "Resolve conflicts",
                                                            "mohist/acp-agent")
                                                    ])
                                            ])),
                                    new TaskDefinition("recover:push", "Push", "mohist/push")
                                ])
                        ]))
            ],
            checks: []));

        // 1. integrate:merge-pr.1 fails base-moved → recover:rebase.1, recover:push.1
        var (mergePr1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, mergePr1.WorkId, new WorkResult("failed", "base moved", Output: """
        {
          "errorCode": "base-moved"
        }
        """));

        var (rebase1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.", rebase1.WorkId);
        Assert.Equal("mohist/rebase", rebase1.Uses);

        // 2. recover:rebase.1 fails with conflict → nested
        //    recover:resolve-rebase-conflicts.1 (engine recurses one
        //    level using the recovery task's own onFailure against its
        //    output).
        await ReportAsync(r2, rebase1.WorkId, new WorkResult("failed", "conflict", Output: """
        {
          "failureKind": "conflict"
        }
        """));

        var (resolveConflicts, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:resolve-rebase-conflicts.", resolveConflicts.WorkId);
        Assert.Equal("mohist/acp-agent", resolveConflicts.Uses);
        await ReportAsync(r3, resolveConflicts.WorkId, "completed");

        // After the nested recovery completes, the enclosing sequence
        // continues with the next task declared after the failed
        // recovery task (recover:push).
        var (push, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:push.", push.WorkId);
        Assert.Equal("mohist/push", push.Uses);
        await ReportAsync(r4, push.WorkId, "completed");

        // The stage has no more pending tasks and no checks (we passed
        // checks: []), so the run transitions to Completed.
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetRunStatusAsync();
        Assert.Equal("Completed", status);

        // integrate:merge-pr.1 and recover:rebase.1 are recorded as
        // failed in the run history; nested recovery resolved the
        // rebase conflict and the enclosing sequence then pushed and
        // the stage completed.
        var run = await LoadRunAsync(_workflowId!);
        var allTasks = run.Stages.Single().Tasks;
        Assert.Contains(allTasks, t => t.DefinitionId == "integrate:merge-pr" && t.Status == TaskRunStatus.Failed);
        Assert.Contains(allTasks, t => t.DefinitionId == "recover:rebase" && t.Status == TaskRunStatus.Failed);
        Assert.Contains(allTasks, t => t.DefinitionId == "recover:resolve-rebase-conflicts" && t.Status == TaskRunStatus.Completed);
        Assert.Contains(allTasks, t => t.DefinitionId == "recover:push" && t.Status == TaskRunStatus.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RecoveryTask_DispatchExposesFailureOutputTemplateContext()
    {
        // A recovery task with a `with` input that references
        // ${{ failure.output.* }} must receive a Variables payload that
        // includes the failed task's raw output under `failure.output.*`.
        // The runner is responsible for template expansion; the engine
        // just threads the payload through.
        var withTemplate = new Dictionary<string, JsonElement?>
        {
            ["marker"] = JsonSerializer.SerializeToElement("${{ failure.output.errorCode }}"),
        };

        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "integrate:merge-pr",
                    "Merge GitHub PR",
                    "mohist/merge-github-pr",
                    OnFailure: new TaskFailureAction(
                        1,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("base-moved")
                                },
                                [
                                    new TaskDefinition(
                                        "recover:rebase",
                                        "Rebase",
                                        "mohist/rebase",
                                        With: withTemplate),
                                ])
                        ]))
            ],
            checks: []));

        var (mergePr, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, mergePr.WorkId, new WorkResult("failed", "base moved", Output: """
        {
          "errorCode": "base-moved",
          "prNumber": 42
        }
        """));

        var (rebase, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.", rebase.WorkId);

        // The dispatch payload includes failure.output.* from the
        // failed task. Variables is the JSON-serialized payload; the
        // runner expands ${{ ... }} from it. We assert the payload
        // contains the failed output under `failure.output.errorCode`.
        Assert.NotNull(rebase.Variables);
        using var variablesDoc = JsonDocument.Parse(rebase.Variables!);
        var root = variablesDoc.RootElement;
        Assert.True(root.TryGetProperty("failure", out var failure), "Variables payload is missing 'failure'");
        Assert.True(failure.TryGetProperty("output", out var failureOutput), "failure is missing 'output'");
        Assert.Equal("base-moved", failureOutput.GetProperty("errorCode").GetString());
        Assert.Equal(42, failureOutput.GetProperty("prNumber").GetInt32());
    }
}
