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

public class FailIfMarkerSpecs : WorkflowGrainSpecs
{
    public FailIfMarkerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_PreservesFailIfInTaskWith()
    {
        // The engine stays failIf-agnostic — it does not interpret
        // expect.markers[*].failIf; the runner does. The serializer's job
        // is to preserve the value through round-trip so the runner sees
        // exactly what the profile author wrote.
        var yaml = """
        stages:
          - stage: check
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/acp-agent
                with:
                  expect:
                    markers:
                      - path: review.md
                        oneOf:
                          - "<promise>PASS</promise>"
                          - "<promise>FAIL</promise>"
                        failIf: "<promise>FAIL</promise>"
            checks: []
        """;

        var definition = WorkflowYamlSerializer.FromYaml(yaml);
        var emitted = WorkflowYamlSerializer.ToYaml(definition);

        Assert.Contains("failIf:", emitted);
        Assert.Contains("oneOf:", emitted);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithExpectMarkersFailIf_ReturnsFailIfAndOneOf()
    {
        // The TaskRun-level required-file extraction should surface the
        // failIf marker as a required file entry carrying oneOf and
        // failIf metadata so downstream views can show "this marker, if
        // matched, fails the task".
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "markers": [
                    {
                      "path": "review.md",
                      "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                      "failIf": "<promise>FAIL</promise>"
                    }
                  ]
                }
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        var entry = Assert.Single(result);
        Assert.Equal("review.md", entry.Path);
        Assert.NotNull(entry.OneOf);
        Assert.Equal(new[] { "<promise>PASS</promise>", "<promise>FAIL</promise>" }, entry.OneOf!);
        Assert.Equal("<promise>FAIL</promise>", entry.FailIf);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithExpectFilesAndMarkers_DedupesByPath()
    {
        // When both `expect.files[*]` and `expect.markers[*]` declare the
        // same path, only one RequiredFile entry is returned. The
        // marker-derived entry wins because it carries the failIf/oneOf
        // metadata.
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "files": [
                    {"path": "review.md", "markers": ["<promise>PASS</promise>"]}
                  ],
                  "markers": [
                    {
                      "path": "review.md",
                      "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                      "failIf": "<promise>FAIL</promise>"
                    }
                  ]
                }
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        var entry = Assert.Single(result);
        Assert.Equal("review.md", entry.Path);
        Assert.Equal("<promise>FAIL</promise>", entry.FailIf);
        Assert.NotNull(entry.OneOf);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskWithFailIfMarker_ReportsErrorCode_TriggersOnFailureRecovery()
    {
        // End-to-end shape: a profile declares a task with an
        // expect.markers[*].failIf binding and an onFailure case that
        // matches output.errorCode. The runner reports the task as
        // failed with the action's own errorCode (here, review-failed);
        // the engine must then treat it like any other task failure and
        // apply the recovery sequence — exactly the same recovery the
        // engine applies to a marker-less failure that emits the same
        // errorCode.
        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "self-review",
                    "Self review",
                    "mohist/acp-agent",
                    OnFailure: new TaskFailureAction(
                        1,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("review-failed")
                                },
                                [
                                    new TaskDefinition(
                                        "recover:fix-plan-review",
                                        "Fix plan review findings",
                                        "mohist/acp-agent")
                                ])
                        ]))
            ],
            checks: []));

        var (selfReview, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("self-review.", selfReview.WorkId);

        // The runner reads the action output (which contains errorCode
        // set from the failIf marker); the engine treats this as an
        // ordinary failed-task report.
        await ReportAsync(r1, selfReview.WorkId, new WorkResult("failed", "failIf marker matched: <promise>FAIL</promise> (errorCode: review-failed)", Output: """
        {
          "errorCode": "review-failed",
          "kind": "acp-agent",
          "status": "failure",
          "failIfMarker": "<promise>FAIL</promise>"
        }
        """));

        var (recovery, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:fix-plan-review.", recovery.WorkId);
        Assert.Equal("mohist/acp-agent", recovery.Uses);
        await ReportAsync(r2, recovery.WorkId, "completed");

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages.Single();
        Assert.Contains(stage.Tasks, t => t.DefinitionId == "self-review" && t.Status == TaskRunStatus.Failed);
        Assert.Contains(stage.Tasks, t => t.DefinitionId == "recover:fix-plan-review" && t.Status == TaskRunStatus.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskWithFailIfMarker_NoMatchingCase_PreservesFailure()
    {
        // The failIf-induced failure carries an errorCode the profile
        // does not match. The engine must treat it like an ordinary task
        // failure with no onFailure case matched — the task remains
        // failed and no recovery is injected.
        await StartWorkflowAsync(SingleStage(
            tasks: [
                new TaskDefinition(
                    "self-review",
                    "Self review",
                    "mohist/acp-agent",
                    OnFailure: new TaskFailureAction(
                        1,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("auth-failed")
                                },
                                [
                                    new TaskDefinition(
                                        "recover:reauth",
                                        "Re-auth",
                                        "mohist/acp-agent")
                                ])
                        ]))
            ],
            checks: []));

        var (selfReview, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, selfReview.WorkId, new WorkResult("failed", "failIf marker matched: <promise>FAIL</promise>", Output: """
        {
          "errorCode": "review-failed",
          "kind": "acp-agent",
          "status": "failure",
          "failIfMarker": "<promise>FAIL</promise>"
        }
        """));

        // No matching onFailure case -> no recovery task.
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetRunStatusAsync();
        Assert.Equal("Failed", status);

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages.Single();
        Assert.Single(stage.Tasks, t => t.DefinitionId == "self-review");
        Assert.DoesNotContain(stage.Tasks, t => t.DefinitionId.StartsWith("recover:"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskPassingViaFailIfMarker_PassesAndRecoveryIsNotInvoked()
    {
        // PASS marker with failIf:FAIL present — the runner does NOT mark
        // the task as failed (failIf only fails when matched). The
        // engine sees a successful task and advances normally; onFailure
        // is not consulted.
        await StartWorkflowAsync(SingleStage(
            stage: "check",
            tasks: [
                new TaskDefinition(
                    "self-review",
                    "Self review",
                    "mohist/acp-agent",
                    OnFailure: new TaskFailureAction(
                        1,
                        [
                            new TaskFailureCase(
                                new Dictionary<string, JsonElement?>
                                {
                                    ["output.errorCode"] = JsonSerializer.SerializeToElement("review-failed")
                                },
                                [
                                    new TaskDefinition(
                                        "recover:fix-plan-review",
                                        "Fix plan review findings",
                                        "mohist/acp-agent")
                                ])
                        ]))
            ],
            checks: [new("plan-artifacts", "Plan artifacts", "mohist/openspec-artifacts")]));

        var (selfReview, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, selfReview.WorkId, new WorkResult("completed", "Self review complete", Output: """
        {
          "errorCode": null,
          "kind": "acp-agent",
          "status": "success",
          "failIfMarker": null,
          "expectation": {"satisfied": true, "failIfMatches": []}
        }
        """));

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-check:", check.WorkId);
        await ReportChecksPassAsync(r2, check, "plan-artifacts");

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages.Single();
        Assert.Contains(stage.Tasks, t => t.DefinitionId == "self-review" && t.Status == TaskRunStatus.Completed);
        Assert.DoesNotContain(stage.Tasks, t => t.DefinitionId.StartsWith("recover:"));
    }
}