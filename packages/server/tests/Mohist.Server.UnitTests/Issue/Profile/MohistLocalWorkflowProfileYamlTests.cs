using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistLocalWorkflowProfileYamlTests
{
    [Fact]
    public void WorkflowYamlParser_CheckLevelRepairFieldsThrowSchemaDiagnostic()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                  timeout: 300000
                repairLimit: 1
                repairTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/acp-agent
                  with:
                    prompt: Fix it
        """));

        Assert.Contains("obsolete check-level repair", ex.Message);
        Assert.Contains("task-level recovery", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_CheckRepairWithVerifyTaskStillThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                repairLimit: 2
                repairTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/acp-agent
                  with:
                    prompt: Fix it
                verifyTask:
                  id: verify-health
                  title: Verify health
                  uses: core/script
                  with:
                    run: git diff --check
        """));

        Assert.Contains("obsolete check-level repair", ex.Message);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDomainDefinition()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(MohistWorkflow.Definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Contains("agent: ${{ vars.agent }}", yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", yaml);
        Assert.DoesNotContain("repairTask:", yaml);
        Assert.DoesNotContain("repairLimit:", yaml);
        Assert.Contains("id: recover:fix-review-findings", yaml);
        Assert.Contains("prompt: ${{ prompts.auto-fix }}", yaml);
        Assert.Contains("retrySelf: true", yaml);
        Assert.DoesNotContain("verifyTask:", yaml);
        Assert.Equal("mohist/openspec-tasks", reparsed.Stages[1].Tasks[1].Uses);
        // Review failure is modeled on the ai-review task itself
        // (failIf + with.recovery + retrySelf), not on a review-passed
        // check. The check stage no longer carries review-passed.
        var checkStage = reparsed.Stages[2];
        Assert.DoesNotContain(checkStage.Checks, c => c.Name == "review-passed");
        var aiReview = checkStage.Tasks.Single(t => t.Id == "ai-review");
        Assert.NotNull(aiReview.Recovery);
        var recovery = aiReview.Recovery!;
        Assert.Equal(2, recovery.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.True(handler.RetrySelf);
        var fixReviewFindings = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-review-findings", fixReviewFindings.Id);
    }

    [Fact]
    public void WorkflowYamlParser_TaskWithNeutralArtifactMarker_ParsesSuccessfully()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks:
              - id: doc-task
                title: Document task
                uses: mohist/acp-agent
                with:
                  prompt: Write docs
                  expect:
                    files:
                      - path: docs/readme.md
                    markers:
                      - path: docs/readme.md
                        contains: "## Getting Started"
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
                uses: mohist/acp-agent
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

        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml(yaml));
        Assert.Contains("verdict marker", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("check definition", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad-task", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_PreservesTaskArtifactCapturePaths()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.proposal }}
                  expect:
                    files:
                      - path: ${{ openspecChangeDir }}/proposal.md
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/proposal.md
                    - path: ${{ openspecChangeDir }}/specs
              - id: design
                title: Design
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.design }}
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/design.md
            checks: []
        """);

        var proposal = definition.Stages.Single().Tasks.Single(t => t.Id == "proposal");
        Assert.NotNull(proposal.Artifacts);
        Assert.Equal(
            new[]
            {
                "${{ openspecChangeDir }}/proposal.md",
                "${{ openspecChangeDir }}/specs",
            },
            proposal.Artifacts!.Files.Select(f => f.Path).ToArray());

        var design = definition.Stages.Single().Tasks.Single(t => t.Id == "design");
        Assert.NotNull(design.Artifacts);
        Assert.Equal(
            new[] { "${{ openspecChangeDir }}/design.md" },
            design.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void WorkflowYamlParser_TaskArtifactsAreNotMergedIntoWith()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare-task
                title: Declare artifacts
                uses: mohist/acp-agent
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
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: expect-only
                title: Expect files only
                uses: mohist/acp-agent
                with:
                  prompt: hello
                  expect:
                    files:
                      - path: docs/expected.md
                    markers:
                      - path: docs/expected.md
                        contains: "# Done"
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Null(task.Artifacts);
    }

    [Fact]
    public void WorkflowYamlParser_AcceptsSamePathInExpectMarkersAndArtifacts()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: review
                title: Review
                uses: mohist/acp-agent
                with:
                  prompt: Review
                  expect:
                    markers:
                      - path: ${{ openspecChangeDir }}/review.md
                        oneOf:
                          - <promise>PASS</promise>
                          - <promise>FAIL</promise>
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/review.md
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(new[] { "${{ openspecChangeDir }}/review.md" }, task.Artifacts!.Files.Select(f => f.Path).ToArray());
        var withJson = JsonSerializer.Serialize(task.With);
        Assert.Contains("expect", withJson);
        Assert.Contains("markers", withJson);
    }

    [Fact]
    public void WorkflowYamlParser_TaskArtifactFileEntryWithoutPathThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: bad
                title: Bad
                uses: mohist/acp-agent
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
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: check
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/acp-agent
                with:
                  prompt: review
                artifacts:
                  files:
                    - path: review.md
                recovery:
                  budget: 1
                  handlers:
                    - when: promise=FAIL
                      tasks:
                        - id: recover:fix-review
                          title: Fix review
                          uses: mohist/acp-agent
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
    public void WorkflowYamlSerializer_RoundTripsTaskArtifactCapture()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare
                title: Declare
                uses: mohist/acp-agent
                with:
                  prompt: hi
                artifacts:
                  files:
                    - path: docs/a.md
                    - path: docs/b.md
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        var task = reparsed.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(
            new[] { "docs/a.md", "docs/b.md" },
            task.Artifacts!.Files.Select(f => f.Path).ToArray());
        Assert.Contains("artifacts:", yaml);
        Assert.Contains("docs/a.md", yaml);
    }
}
