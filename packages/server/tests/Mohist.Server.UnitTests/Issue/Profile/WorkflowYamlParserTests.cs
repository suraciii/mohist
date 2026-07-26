using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class WorkflowYamlParserTests
{
    [Fact]
    public void WorkflowYamlParser_MalformedYaml_ReturnsDefinitionDiagnostic()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("stages: [", "broken"));

        var error = Assert.Single(exception.Errors);
        Assert.Equal(ValidationSource.Definition, error.Source);
        Assert.Equal(string.Empty, error.Path);
        Assert.Contains("invalid YAML", error.Message);
    }

    [Fact]
    public void WorkflowYamlParser_CheckLevelRepairFieldsThrowSchemaDiagnostic()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - id: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                  timeout: 300000
                repairLimit: 1
                repairTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/opencode
                  with:
                    prompt: Fix it
        """));

        Assert.Contains("repairLimit", ex.Message);
        Assert.Contains("unknown field", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_CheckRepairWithVerifyTaskStillThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - id: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                repairLimit: 2
                repairTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/opencode
                  with:
                    prompt: Fix it
                verifyTask:
                  id: verify-health
                  title: Verify health
                  uses: core/script
                  with:
                    run: git diff --check
        """));

        Assert.Contains("repairLimit", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_TaskWithNeutralArtifactMarker_ParsesSuccessfully()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks:
              - id: doc-task
                title: Document task
                uses: mohist/opencode
                with:
                  prompt: Write docs
                expect:
                  files:
                    - path: docs/readme.md
                  markers:
                    - path: docs/readme.md
                      oneOf:
                        - "## Getting Started"
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Equal("doc-task", task.Id);
    }

    [Theory]
    [InlineData("PASS")]
    [InlineData("FAIL")]
    [InlineData("<promise>PASS</promise>")]
    [InlineData("<promise>FAIL</promise>")]
    public void WorkflowYamlParser_TaskWithVerdictMarkerInExpect_ThrowsSchemaDiagnostic(string marker)
    {
        var yaml = $"""
        stages:
          - stage: build
            tasks:
              - id: bad-task
                title: Bad task
                uses: mohist/opencode
                with:
                  prompt: Do work
                expect:
                  files:
                    - path: result.md
                  markers:
                    - path: result.md
                      contains: {marker}
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("verdict marker", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("markers[0]", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_PreservesTaskArtifactCapturePaths()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/opencode
                with:
                  prompt: ${{ prompts.proposal }}
                expect:
                  files:
                    - path: openspec/changes/issue-${{ issue.number }}/proposal.md
                artifacts:
                  files:
                    - path: openspec/changes/issue-${{ issue.number }}/proposal.md
                    - path: openspec/changes/issue-${{ issue.number }}/specs
              - id: design
                title: Design
                uses: mohist/opencode
                with:
                  prompt: ${{ prompts.design }}
                artifacts:
                  files:
                    - path: openspec/changes/issue-${{ issue.number }}/design.md
            checks: []
        """);

        var proposal = definition.Stages.Single().Tasks.Single(t => t.Id == "proposal");
        Assert.NotNull(proposal.Artifacts);
        Assert.Equal(
            new[]
            {
                "openspec/changes/issue-${{ issue.number }}/proposal.md",
                "openspec/changes/issue-${{ issue.number }}/specs",
            },
            proposal.Artifacts!.Files.Select(f => f.Path).ToArray());

        var design = definition.Stages.Single().Tasks.Single(t => t.Id == "design");
        Assert.NotNull(design.Artifacts);
        Assert.Equal(
            new[] { "openspec/changes/issue-${{ issue.number }}/design.md" },
            design.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void WorkflowYamlParser_TaskArtifactsAreNotMergedIntoWith()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare-task
                title: Declare artifacts
                uses: mohist/opencode
                with:
                  prompt: hello
                artifacts:
                  files:
                    - path: docs/out.md
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.With);
        var withJson = JsonSerializer.Serialize(task.With);
        Assert.DoesNotContain("artifacts", withJson);
        Assert.DoesNotContain("docs/out.md", withJson);

        Assert.NotNull(task.Artifacts);
        Assert.Equal(new[] { "docs/out.md" }, task.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void WorkflowYamlParser_WithExpectFilesAloneDoesNotCreateArtifactCapture()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: expect-only
                title: Expect files only
                uses: mohist/opencode
                with:
                  prompt: hello
                expect:
                  files:
                    - path: docs/expected.md
                  markers:
                     - path: docs/expected.md
                       oneOf:
                         - "# Done"
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Null(task.Artifacts);
    }

    [Fact]
    public void WorkflowYamlParser_AcceptsSamePathInExpectMarkersAndArtifacts()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: review
                title: Review
                uses: mohist/opencode
                with:
                  prompt: Review
                expect:
                  markers:
                    - path: openspec/changes/issue-${{ issue.number }}/review.md
                      oneOf:
                        - <promise>PASS</promise>
                        - <promise>FAIL</promise>
                artifacts:
                  files:
                    - path: openspec/changes/issue-${{ issue.number }}/review.md
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(new[] { "openspec/changes/issue-${{ issue.number }}/review.md" }, task.Artifacts!.Files.Select(f => f.Path).ToArray());
        Assert.NotNull(task.Expect);
        var expectJson = JsonSerializer.Serialize(task.Expect);
        Assert.Contains("markers", expectJson);
        Assert.DoesNotContain("expect", JsonSerializer.Serialize(task.With));
    }

    [Fact]
    public void WorkflowYamlParser_TaskArtifactFileEntryWithoutPathThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: bad
                title: Bad
                uses: mohist/opencode
                with:
                  prompt: hi
                artifacts:
                  files:
                    - other: docs/out.md
            checks: []
        """));

        Assert.Contains("artifacts.files", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_RecoveryTaskArtifactsAreIsolated()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: check
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/opencode
                with:
                  prompt: review
                artifacts:
                  files:
                    - path: review.md
                recovery:
                  budget: 1
                  handlers:
                    - when: output.promise=FAIL
                      tasks:
                        - id: recover:fix-review
                          title: Fix review
                          uses: mohist/opencode
                          with:
                            prompt: fix
                      retrySelf: true
            checks: []
        """);

        var stage = definition.Stages.Single();
        var review = stage.Tasks.Single();
        Assert.NotNull(review.Artifacts);

        var recoveryTask = Assert.Single(Assert.Single(review.Recovery!.Handlers).Tasks);
        Assert.Null(recoveryTask.Artifacts);
    }

    [Fact]
    public void WorkflowYamlParser_AllowsLastRecoveryHandlerWithoutWhenAndOmitsItOnRoundTrip()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks:
              - id: verify
                title: Verify
                uses: core/script
                recovery:
                  budget: 1
                  handlers:
                    - when: error.code=script-failed
                      retrySelf: true
                    - tasks:
                        - id: recover:fix-ci
                          title: Fix CI
                          uses: mohist/opencode
                      retrySelf: true
            checks: []
        """);

        var handlers = definition.Stages.Single().Tasks.Single().Recovery!.Handlers;
        Assert.Equal("error.code=script-failed", handlers[0].When);
        Assert.Null(handlers[1].When);

        var emitted = WorkflowYamlSerializer.ToYaml(definition);
        Assert.Contains("when: error.code=script-failed", emitted);
        Assert.DoesNotContain("when: null", emitted);
    }

    [Fact]
    public void WorkflowYamlParser_RejectsDefaultRecoveryHandlerBeforeExplicitHandler()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks:
              - id: verify
                title: Verify
                uses: core/script
                recovery:
                  handlers:
                    - retrySelf: true
                    - when: error.code=script-failed
                      retrySelf: true
            checks: []
        """));

        Assert.Contains("default handler (without 'when') must be last", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_DefinitionDoesNotCarryDescription()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.DoesNotContain("description", WorkflowYamlSerializer.ToYaml(definition), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowYamlParser_DefinitionDescriptionIsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        description: Simple description
        stages:
          - stage: build
            tasks: []
            checks: []
        """));

        Assert.Contains("description", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_ParsesApprovalFeedbackTasksConfig()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        approval:
          feedback:
            tasks:
              - id: apply-feedback
                title: Apply approval feedback
                uses: mohist/opencode
                with:
                  session: ${{ stage.name }}
                  prompt: ${{ prompts.apply-feedback }}
        stages:
          - stage: plan
            tasks: []
            checks: []
        """);

        Assert.NotNull(definition.Approval);
        Assert.NotNull(definition.Approval!.Feedback);
        var task = Assert.Single(definition.Approval!.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("Apply approval feedback", task.Title);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.NotNull(task.With);
        Assert.True(task.With!.ContainsKey("session"));
        Assert.True(task.With!.ContainsKey("prompt"));
        Assert.Equal("${{ stage.name }}", task.With["session"]?.GetString());
        Assert.Equal("${{ prompts.apply-feedback }}", task.With["prompt"]?.GetString());
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalSectionAbsent_ReturnsNullApproval()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Null(definition.Approval);
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalFeedbackTaskMissingId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        approval:
          feedback:
            tasks:
              - title: Apply approval feedback
                uses: mohist/opencode
        stages:
          - stage: build
            tasks: []
            checks: []
        """));

        Assert.Contains("task identifier is required", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalFeedbackTaskMissingTitle_Throws()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        approval:
          feedback:
            tasks:
              - id: apply-feedback
                uses: mohist/opencode
        stages:
          - stage: build
            tasks: []
            checks: []
        """);
        Assert.Null(definition.Approval!.Feedback!.Tasks![0].Title);
    }

}
