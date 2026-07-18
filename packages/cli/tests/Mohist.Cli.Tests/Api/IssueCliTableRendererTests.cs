using System.Net;
using Mohist.Cli.Tests.Compatibility;
using System.Text;
using System.Text.Json.Nodes;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class IssueCliTableRendererTests
{
    [Fact]
    public async Task PrintWithOutputAsync_Table_SendsSameHttpRequestAsJson()
    {
        var jsonHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local", "baseBranch": "master" }] }
            """);
        var tableHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local", "baseBranch": "master" }] }
            """);

        var jsonApi = BuildApi(jsonHandler);
        await jsonApi.PrintWithOutputAsync("/api/projects", "json");

        var tableApi = BuildApi(tableHandler);
        await tableApi.PrintWithOutputAsync("/api/projects", "table", "ProjectList");

        var jsonReq = jsonHandler.Requests.Single();
        var tableReq = tableHandler.Requests.Single();

        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects", tableReq.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task RenderTable_ProjectList_ContainsIdNameBaseBranch_AndMarksActive()
    {
        var data = JsonNode.Parse("""
            [
              { "id": "proj_aaa", "name": "alpha", "baseBranch": "main" },
              { "id": "proj_bbb", "name": "beta",  "baseBranch": "dev" }
            ]
            """);

        var output = new StringWriter();
        var fs = new FakeFileSystem();
        await fs.WriteAllTextAsync(
            "/home/test/.mohist/cli-state.json",
            """{ "activeProjectId": "proj_bbb" }""");
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            fs,
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.ProjectList);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("name", text);
        Assert.Contains("base branch", text);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
        Assert.Contains("proj_bbb", text);
        Assert.Contains("*", text);
    }

    [Fact]
    public async Task RenderTable_ProjectShow_IsMultiLineSummary()
    {
        var data = JsonNode.Parse("""
            {
              "id": "proj_x",
              "name": "demo",
              "baseBranch": "master",
              "repositories": [{ "name": "main" }, { "name": "alt" }],
              "createdAt": "2026-01-01T00:00:00Z",
              "updatedAt": "2026-02-01T00:00:00Z"
            }
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.ProjectShow);

        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("name:", text);
        Assert.Contains("base branch:", text);
        Assert.Contains("repositories:", text);
        Assert.Contains("2", text);
        Assert.Contains("created:", text);
        Assert.Contains("updated:", text);
        Assert.Contains("demo", text);
        Assert.Contains("proj_x", text);
    }

    [Fact]
    public async Task RenderTable_IssueList_ContainsNumberTitleStageStatusPriority_TruncatesLongTitle()
    {
        var longTitle = new string('x', 120);
        var data = JsonNode.Parse($$"""
            [
              { "number": 1, "title": "{{longTitle}}", "workflowStage": "build", "status": "in_progress", "priority": "p1" },
              { "number": 2, "title": "short", "workflowStage": "plan", "status": "backlog", "priority": "p3" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueList);

        var text = output.ToString();
        Assert.Contains("number", text);
        Assert.Contains("title", text);
        Assert.Contains("stage", text);
        Assert.Contains("status", text);
        Assert.Contains("priority", text);
        Assert.Contains("build", text);
        Assert.Contains("in_progress", text);
        Assert.Contains("p1", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longTitle, text);
    }

    [Fact]
    public async Task RenderTable_IssueShow_IsMultiLineSummaryWithCondensedBody()
    {
        var longBody = new string('B', 200);
        var data = JsonNode.Parse($$"""
            {
              "number": 83,
              "title": "Expand Mohist CLI ergonomics for project-scoped work",
              "workflowStage": "build",
              "status": "in_progress",
              "priority": "p1",
              "projectName": "mohist-local",
              "updatedAt": "2026-06-11T08:34:11.158Z",
              "body": "{{longBody}}"
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("number:", text);
        Assert.Contains("title:", text);
        Assert.Contains("stage:", text);
        Assert.Contains("status:", text);
        Assert.Contains("priority:", text);
        Assert.Contains("project:", text);
        Assert.Contains("updated:", text);
        Assert.Contains("body:", text);
        Assert.Contains("mohist-local", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longBody, text);
    }

    [Fact]
    public async Task RenderTable_IssueShow_RendersParentReferenceAndChildProgress()
    {
        var data = JsonNode.Parse("""
            {
              "number": 200,
              "title": "Composite parent",
              "workflowStage": "build",
              "status": "inProgress",
              "priority": "p1",
              "projectName": "mohist-local",
              "updatedAt": "2026-06-11T08:34:11.158Z",
              "parentIssueRef": null,
              "childIssuesSummary": {
                "hasChildren": true,
                "count": 4,
                "backlogCount": 1,
                "inProgressCount": 1,
                "doneCount": 2,
                "cancelledCount": 0
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("parent:   is a parent (4 child issues)", text);
        Assert.Contains("children: 2 done / 1 in-progress / 0 cancelled / 1 backlog / 4 total", text);
    }

    [Fact]
    public async Task RenderTable_IssueShow_RendersParentIssueReferenceForChild()
    {
        var data = JsonNode.Parse("""
            {
              "number": 201,
              "title": "Child issue",
              "workflowStage": "plan",
              "status": "backlog",
              "priority": "p2",
              "projectName": "mohist-local",
              "updatedAt": "2026-06-11T08:34:11.158Z",
              "parentIssueRef": { "number": 200, "title": "Composite parent" },
              "childIssuesSummary": null
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("parent:   #200 Composite parent", text);
        Assert.DoesNotContain("children:", text);
        Assert.DoesNotContain("is a parent", text);
    }

    [Fact]
    public async Task RenderTable_WorkflowStatus_SummarizesCurrentStageTaskStatesAndWaiting()
    {
        var data = JsonNode.Parse("""
            {
              "issueId": "iss_83",
              "issueNumber": 83,
              "title": "Expand Mohist CLI ergonomics",
              "stage": "check",
              "runtimeStatus": "running",
              "workflowRunId": "wr_83",
              "workflow": {
                "workflowRunId": "wr_83",
                "status": "running",
                "currentStage": "build",
                "stages": [
                  { "stage": "plan",  "status": "completed", "tasks": [ { "status": "completed" }, { "status": "completed" } ], "approvalStatus": { "result": "approved" } },
                  { "stage": "build", "status": "running",   "tasks": [ { "status": "completed" }, { "status": "pending" } ], "approvalStatus": null },
                  { "stage": "check", "status": "pending",   "tasks": [], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.Contains("current stage: build", text);
        Assert.Contains("status:        running", text);
        Assert.Contains("plan", text);
        Assert.Contains("approved", text);
        Assert.Contains("pending", text);
    }

    [Fact]
    public async Task RenderTable_Sessions_ListsIdStateStartedModel()
    {
        var data = JsonNode.Parse("""
            [
              { "sessionName": "T-006.1", "status": "running",   "createdAt": "2026-06-11T08:34:11Z", "model": "minimax-coding-plan/MiniMax-M3" },
              { "sessionName": "T-005.1", "status": "completed", "createdAt": "2026-06-11T08:00:00Z", "model": "minimax-coding-plan/MiniMax-M3" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.Sessions);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("state", text);
        Assert.Contains("started", text);
        Assert.Contains("model", text);
        Assert.Contains("T-006.1", text);
        Assert.Contains("running", text);
        Assert.Contains("completed", text);
        Assert.Contains("minimax-coding-plan/MiniMax-M3", text);
    }

    [Fact]
    public async Task RenderTable_RepoList_ListsNameGitUrlBaseBranchAndIsDefault()
    {
        var data = JsonNode.Parse("""
            [
              { "name": "master", "gitUrl": "git@example.com:repo.git", "baseBranch": "master", "isDefault": true },
              { "name": "alt",    "gitUrl": "git@example.com:alt.git",  "baseBranch": "main",   "isDefault": false }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.RepoList);

        var text = output.ToString();
        Assert.Contains("name", text);
        Assert.Contains("git URL", text);
        Assert.Contains("base branch", text);
        Assert.Contains("default", text);
        Assert.Contains("master", text);
        Assert.Contains("alt", text);
        Assert.Contains("git@example.com:repo.git", text);
        Assert.Contains("yes", text);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncate_RespectsSoftCaps_60And24()
    {
        var long60 = new string('a', 80);
        var truncated60 = InvokeTruncate(long60, 60);
        Assert.Equal(60, truncated60.Length);
        Assert.EndsWith("…", truncated60);

        var long24 = new string('b', 40);
        var truncated24 = InvokeTruncate(long24, 24);
        Assert.Equal(24, truncated24.Length);
        Assert.EndsWith("…", truncated24);

        var shortValue = "ok";
        Assert.Equal("ok", InvokeTruncate(shortValue, 60));
    }

    [Fact]
    public void Truncate_OnlyFirstLineIsKept()
    {
        var multiline = "first line\nsecond line that should be discarded";
        var result = InvokeTruncate(multiline, 60);
        Assert.Equal("first line", result);
    }

    [Fact]
    public void ParseTableShape_AcceptsKnownAndDefaultsOnUnknown()
    {
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape(null));
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape(""));
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape("Unknown"));
        Assert.Equal(MohistCliApi.TableShape.IssueList, MohistCliApi.ParseTableShape("IssueList"));
        Assert.Equal(MohistCliApi.TableShape.RepoList, MohistCliApi.ParseTableShape("RepoList"));
    }

    public static IEnumerable<object[]> DeliveryFailureKindMatrix() => new List<object[]>
    {
        new object[] { DeliveryFailureGuidance.Conflict, "Conflict needs attention", "Conflicts could not be resolved automatically." },
        new object[] { DeliveryFailureGuidance.BaseMoved, "Base branch moved", "Prepare the branch again, then publish." },
        new object[] { DeliveryFailureGuidance.RetrySafe, "Transient failure", "Retry the task" },
        new object[] { DeliveryFailureGuidance.BranchInvariantViolation, "Runner / action branch-invariant violation", "runner or action bug" },
        new object[] { DeliveryFailureGuidance.WorkspaceSetup, "Workflow workspace setup failure", "could not prepare the workflow workspace" },
        new object[] { DeliveryFailureGuidance.ConfigError, "Runner environment is misconfigured", "gh auth login" },
        new object[] { DeliveryFailureGuidance.ProtectionConflict, "Branch protection blocked the merge", "branch protection" },
        new object[] { DeliveryFailureGuidance.PrStateConflict, "Pull request state changed externally", "auto-retry this kind" },
        new object[] { DeliveryFailureGuidance.PrChecksUnavailable, "Pull request checks unavailable", "will not trigger code recovery" },
    };

    [Theory]
    [MemberData(nameof(DeliveryFailureKindMatrix))]
    public void DeliveryFailureGuidance_ResolveGuidance_ExposesLabelAndNextActionForAllKinds(
        string kind, string expectedLabel, string expectedActionFragment)
    {
        var guidance = DeliveryFailureGuidance.ResolveGuidance(kind);

        Assert.NotNull(guidance);
        Assert.Equal(expectedLabel, guidance!.Value.Label);
        Assert.Contains(expectedActionFragment, guidance.Value.NextAction);
    }

    [Theory]
    [InlineData("Prepare failed (conflict): CONFLICT in foo.ts", DeliveryFailureGuidance.Conflict, "Conflict needs attention")]
    [InlineData("Publish failed (base-moved): non-fast-forward", DeliveryFailureGuidance.BaseMoved, "Base branch moved")]
    [InlineData("Prepare failed (retry-safe): network reset", DeliveryFailureGuidance.RetrySafe, "Transient failure")]
    [InlineData("Prepare failed (branch-invariant-violation): wrong branch", DeliveryFailureGuidance.BranchInvariantViolation, "Runner / action branch-invariant violation")]
    [InlineData("branch-invariant violation at start boundary for Prepare branch: expected branch 'mohist/run-wr-1', observed 'master'", DeliveryFailureGuidance.BranchInvariantViolation, "Runner / action branch-invariant violation")]
    [InlineData("could not prepare workflow workspace (workspace-setup): git clone failed for ...", DeliveryFailureGuidance.WorkspaceSetup, "Workflow workspace setup failure")]
    [InlineData("Publish failed (config-error): gh CLI is not installed", DeliveryFailureGuidance.ConfigError, "Runner environment is misconfigured")]
    [InlineData("Publish failed (protection-conflict): required status checks missing", DeliveryFailureGuidance.ProtectionConflict, "Branch protection blocked the merge")]
    [InlineData("Publish failed (pr-state-conflict): PR was closed externally", DeliveryFailureGuidance.PrStateConflict, "Pull request state changed externally")]
    [InlineData("Merge failed (pr-checks-unavailable): GraphQL EOF", DeliveryFailureGuidance.PrChecksUnavailable, "Pull request checks unavailable")]
    public void DeliveryFailureGuidance_ResolveFailureKindFromMessage_ExtractsKind(
        string message, string expectedKind, string expectedLabel)
    {
        var (kind, guidance) = DeliveryFailureGuidance.Resolve(message, output: null);

        Assert.Equal(expectedKind, kind);
        Assert.NotNull(guidance);
        Assert.Equal(expectedLabel, guidance!.Value.Label);
    }

    [Theory]
    [InlineData(DeliveryFailureGuidance.Conflict)]
    [InlineData(DeliveryFailureGuidance.BaseMoved)]
    [InlineData(DeliveryFailureGuidance.RetrySafe)]
    [InlineData(DeliveryFailureGuidance.ConfigError)]
    [InlineData(DeliveryFailureGuidance.ProtectionConflict)]
    [InlineData(DeliveryFailureGuidance.PrStateConflict)]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_ExtractsKind(string kind)
    {
        var taskKind = kind switch
        {
            DeliveryFailureGuidance.BaseMoved => "publish",
            DeliveryFailureGuidance.ConfigError => "publish-via-pr",
            DeliveryFailureGuidance.ProtectionConflict => "publish-via-pr",
            DeliveryFailureGuidance.PrStateConflict => "publish-via-pr",
            _ => "prepare",
        };
        var output = JsonNode.Parse($$"""
            {
              "kind": "{{taskKind}}",
              "status": "failed",
              "failureKind": "{{kind}}"
            }
            """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(kind, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_ExtractsErrorCode()
    {
        var output = JsonNode.Parse("""
        {
          "kind": "merge-pull-request",
          "status": "failed",
          "errorCode": "base-moved"
        }
        """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(DeliveryFailureGuidance.BaseMoved, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_ExtractsBranchInvariantViolationKind()
    {
        var output = JsonNode.Parse("""
            {
              "kind": "prepare",
              "status": "failed",
              "failureKind": "branch-invariant-violation",
              "boundary": "start",
              "expectedBranch": "mohist/run-wr-1",
              "observedBranch": "master"
            }
            """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(DeliveryFailureGuidance.BranchInvariantViolation, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_RecursesIntoBranchStabilityEvidence()
    {
        var output = JsonNode.Parse("""
            {
              "branchStability": [
                { "kind": "branch-stability", "boundary": "start", "expectedBranch": "mohist/run-wr-1", "observedBranch": "mohist/run-wr-1" },
                { "kind": "branch-invariant-violation", "boundary": "end", "expectedBranch": "mohist/run-wr-1", "observedBranch": "master" }
              ]
            }
            """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(DeliveryFailureGuidance.BranchInvariantViolation, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveBranchEvidence_FromRunnerOutput()
    {
        var output = JsonNode.Parse("""
            {
              "kind": "branch-invariant-violation",
              "boundary": "start",
              "expectedBranch": "mohist/run-wr-1",
              "observedBranch": "master"
            }
            """);

        var evidence = DeliveryFailureGuidance.ResolveBranchEvidence(message: null, output: output);

        Assert.NotNull(evidence);
        Assert.Equal("mohist/run-wr-1", evidence!.ExpectedBranch);
        Assert.Equal("master", evidence.ObservedBranch);
        Assert.Equal("start", evidence.Boundary);
        Assert.Null(evidence.ObservedRef);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveBranchEvidence_FromRunnerMessage()
    {
        var message =
            "branch-invariant violation at end boundary for Publish changes: expected branch 'mohist/run-wr-1', observed 'master'";

        var evidence = DeliveryFailureGuidance.ResolveBranchEvidence(message, output: null);

        Assert.NotNull(evidence);
        Assert.Equal("mohist/run-wr-1", evidence!.ExpectedBranch);
        Assert.Equal("master", evidence.ObservedBranch);
        Assert.Equal("end", evidence.Boundary);
        Assert.Null(evidence.ObservedRef);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveBranchEvidence_DetachedHeadFromMessage()
    {
        var message =
            "branch-invariant violation at start boundary for Prepare branch: expected branch 'mohist/run-wr-1', observed detached at abc123";

        var evidence = DeliveryFailureGuidance.ResolveBranchEvidence(message, output: null);

        Assert.NotNull(evidence);
        Assert.Equal("mohist/run-wr-1", evidence!.ExpectedBranch);
        Assert.Equal(string.Empty, evidence.ObservedBranch);
        Assert.Equal("start", evidence.Boundary);
        Assert.Equal("abc123", evidence.ObservedRef);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveBranchEvidence_ReturnsNullForUnrelatedFailure()
    {
        var evidence = DeliveryFailureGuidance.ResolveBranchEvidence(
            "Prepare failed (conflict): CONFLICT in foo.ts",
            output: null);

        Assert.Null(evidence);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromMessage_DoesNotMistakeOtherFailuresForBranchInvariantViolation()
    {
        var (kind, _) = DeliveryFailureGuidance.Resolve(
            "Prepare failed (dirty-worktree): staged changes left behind",
            output: null);
        Assert.Null(kind);

        var (conflict, _) = DeliveryFailureGuidance.Resolve(
            "Prepare failed (provider-failure): network reset",
            output: null);
        Assert.Null(conflict);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_RecursesIntoNestedOutputField()
    {
        var output = JsonNode.Parse("""
            {
              "output": "{\"failureKind\":\"retry-safe\"}"
            }
            """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(DeliveryFailureGuidance.RetrySafe, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromMessage_ReturnsNullForUnknownKind()
    {
        var (kind, guidance) = DeliveryFailureGuidance.Resolve("Prepare failed (something-else): foo", output: null);

        Assert.Null(kind);
        Assert.Null(guidance);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromEmpty_ReturnsNull()
    {
        Assert.Null(DeliveryFailureGuidance.ResolveFailureKind((string?)null));
        Assert.Null(DeliveryFailureGuidance.ResolveFailureKind((JsonNode?)null));
        Assert.Null(DeliveryFailureGuidance.ResolveFailureKind(string.Empty));
    }

    [Theory]
    [InlineData("Prepare failed (conflict): CONFLICT in foo.ts", DeliveryFailureGuidance.Conflict)]
    [InlineData("Publish failed (base-moved): non-fast-forward", DeliveryFailureGuidance.BaseMoved)]
    [InlineData("Prepare failed (retry-safe): network reset", DeliveryFailureGuidance.RetrySafe)]
    [InlineData("Publish failed (config-error): gh CLI is not installed", DeliveryFailureGuidance.ConfigError)]
    [InlineData("Publish failed (protection-conflict): required status checks missing", DeliveryFailureGuidance.ProtectionConflict)]
    [InlineData("Publish failed (pr-state-conflict): PR was closed externally", DeliveryFailureGuidance.PrStateConflict)]
    [InlineData("Merge failed (pr-checks-unavailable): GraphQL EOF", DeliveryFailureGuidance.PrChecksUnavailable)]
    public async Task RenderTable_WorkflowStatus_SurfacesDeliveryFailureKindLabelAndNextAction(
        string failureMessage, string expectedKind)
    {
        var data = JsonNode.Parse($$"""
            {
              "issueId": "iss_141",
              "issueNumber": 141,
              "title": "Split Integrate delivery",
              "stage": "integrate",
              "runtimeStatus": "failed",
              "workflowRunId": "wr_141",
              "workflow": {
                "workflowRunId": "wr_141",
                "status": "failed",
                "currentStage": "integrate",
                "failure": {
                  "reason": "TaskFailed",
                  "stage": "integrate",
                  "taskId": "integrate:prepare",
                  "message": "{{failureMessage}}"
                },
                "stages": [
                  { "stage": "integrate", "status": "failed", "tasks": [ { "status": "failed" } ], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.Contains("delivery failure:", text);
        Assert.Contains(expectedKind, text);
        Assert.Contains("next action:", text);

        var resolved = DeliveryFailureGuidance.Resolve(failureMessage, null);
        var label = resolved.Guidance!.Value.Label;
        Assert.Contains(label, text);
    }

    [Fact]
    public async Task RenderTable_WorkflowStatus_OmitsDeliveryFailureSectionWhenKindUnknown()
    {
        var data = JsonNode.Parse("""
            {
              "issueId": "iss_141",
              "issueNumber": 141,
              "title": "Split Integrate delivery",
              "stage": "integrate",
              "runtimeStatus": "failed",
              "workflowRunId": "wr_141",
              "workflow": {
                "workflowRunId": "wr_141",
                "status": "failed",
                "currentStage": "integrate",
                "failure": {
                  "reason": "TaskFailed",
                  "stage": "integrate",
                  "taskId": "integrate:publish",
                  "message": "Some other failure without a kind"
                },
                "stages": [
                  { "stage": "integrate", "status": "failed", "tasks": [ { "status": "failed" } ], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.DoesNotContain("delivery failure:", text);
    }

    [Fact]
    public async Task RenderTable_WorkflowStatus_SurfacesBranchInvariantViolationWithEvidenceFromMessage()
    {
        // The runner emits a plain-text message containing "branch-invariant violation"
        // and the expected/observed branch names. The CLI must surface it as a runner/action
        // bug with the evidence block and must NOT collapse it into a generic dirty-worktree,
        // conflict, base-moved, retry-safe, or provider failure.
        var failureMessage =
            "branch-invariant violation at start boundary for Prepare branch: expected branch 'mohist/run-wr-1', observed 'master'";
        var data = JsonNode.Parse($$"""
            {
              "issueId": "iss_150",
              "issueNumber": 150,
              "title": "Keep workflow execution on the run branch",
              "stage": "integrate",
              "runtimeStatus": "failed",
              "workflowRunId": "wr_150",
              "workflow": {
                "workflowRunId": "wr_150",
                "status": "failed",
                "currentStage": "integrate",
                "failure": {
                  "reason": "TaskFailed",
                  "stage": "integrate",
                  "taskId": "integrate:prepare",
                  "message": "{{failureMessage}}"
                },
                "stages": [
                  { "stage": "integrate", "status": "failed", "tasks": [ { "status": "failed" } ], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.Contains("delivery failure:", text);
        Assert.Contains(DeliveryFailureGuidance.BranchInvariantViolation, text);
        Assert.Contains("next action:", text);
        Assert.Contains("Runner / action branch-invariant violation", text);
        Assert.Contains("attribution: runner/action (not issue work)", text);
        Assert.Contains("boundary:   start", text);
        Assert.Contains("expected:   mohist/run-wr-1", text);
        Assert.Contains("observed:   master", text);
        // The new kind must NOT be confused with other delivery failure kinds.
        Assert.DoesNotContain("Conflict needs attention", text);
        Assert.DoesNotContain("Base branch moved", text);
        Assert.DoesNotContain("Transient failure", text);
    }

    [Fact]
    public async Task RenderTable_WorkflowStatus_SurfacesBranchInvariantViolationDetachedHead()
    {
        // A detached HEAD at a boundary is itself a branch-invariant violation.
        // The renderer must surface it with the (detached at <ref>) shape instead
        // of an observed branch name.
        var failureMessage =
            "branch-invariant violation at end boundary for Publish changes: expected branch 'mohist/run-wr-1', observed detached at abc123";
        var data = JsonNode.Parse($$"""
            {
              "issueId": "iss_150",
              "issueNumber": 150,
              "title": "Keep workflow execution on the run branch",
              "stage": "integrate",
              "runtimeStatus": "failed",
              "workflowRunId": "wr_150",
              "workflow": {
                "workflowRunId": "wr_150",
                "status": "failed",
                "currentStage": "integrate",
                "failure": {
                  "reason": "TaskFailed",
                  "stage": "integrate",
                  "taskId": "integrate:publish",
                  "message": "{{failureMessage}}"
                },
                "stages": [
                  { "stage": "integrate", "status": "failed", "tasks": [ { "status": "failed" } ], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.Contains(DeliveryFailureGuidance.BranchInvariantViolation, text);
        Assert.Contains("boundary:   end", text);
        Assert.Contains("expected:   mohist/run-wr-1", text);
        Assert.Contains("observed:   (detached at abc123)", text);
    }

    [Theory]
    [InlineData(DeliveryFailureGuidance.WorkspaceSetup)]
    public void DeliveryFailureGuidance_ResolveFailureKindFromOutput_ExtractsWorkspaceSetupKind(string kind)
    {
        var output = JsonNode.Parse($$"""
            {
              "kind": "{{kind}}",
              "status": "failed"
            }
            """);

        var resolved = DeliveryFailureGuidance.ResolveFailureKind(output);

        Assert.Equal(kind, resolved);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveWorkspaceEvidence_FromRunnerOutput()
    {
        var output = JsonNode.Parse("""
            {
              "kind": "workspace-setup",
              "workspacePath": "/home/runner/workspaces/proj_x/run_abc"
            }
            """);

        var (kind, guidance, evidence) = DeliveryFailureGuidance.ResolveWithWorkspaceEvidence(message: null, output);

        Assert.Equal(DeliveryFailureGuidance.WorkspaceSetup, kind);
        Assert.NotNull(guidance);
        Assert.NotNull(evidence);
        Assert.Equal("/home/runner/workspaces/proj_x/run_abc", evidence!.WorkspacePath);
    }

    [Fact]
    public void DeliveryFailureGuidance_IsWorkspaceSetupKind_RecognisesWorkspaceSetupKind()
    {
        Assert.True(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.WorkspaceSetup));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.Conflict));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.BaseMoved));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.RetrySafe));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.BranchInvariantViolation));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.ConfigError));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.ProtectionConflict));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(DeliveryFailureGuidance.PrStateConflict));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(null));
        Assert.False(DeliveryFailureGuidance.IsWorkspaceSetupKind(""));
    }

    [Theory]
    [InlineData(DeliveryFailureGuidance.WorkspaceSetup)]
    public async Task RenderTable_WorkflowStatus_SurfacesWorkspaceSetupFailureKindLabelAndNextAction(string kind)
    {
        var failureMessage = $"could not prepare workflow workspace ({kind}): git clone failed for run wr_abc";
        var data = JsonNode.Parse($$"""
            {
              "issueId": "iss_181",
              "issueNumber": 181,
              "title": "Materialize workflow workspace once at run start",
              "stage": "build",
              "runtimeStatus": "failed",
              "workflowRunId": "wr_abc",
              "workflow": {
                "workflowRunId": "wr_abc",
                "status": "failed",
                "currentStage": "build",
                "failure": {
                  "reason": "TaskFailed",
                  "stage": "build",
                  "taskId": "build:task-1",
                  "message": "{{failureMessage}}",
                  "output": "{\"kind\":\"{{kind}}\",\"workspacePath\":\"/home/runner/workspaces/proj_x/wr_abc\"}"
                },
                "stages": [
                  { "stage": "build", "status": "failed", "tasks": [ { "status": "failed" } ], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        // The runner-side workspace-setup failure must be rendered as a
        // distinct workflow-infrastructure failure, NOT collapsed into a
        // generic task failure.
        Assert.Contains("delivery failure:", text);
        Assert.Contains(kind, text);
        Assert.Contains("Workflow workspace setup failure", text);
        Assert.Contains("attribution: workflow infrastructure (not issue work)", text);
        Assert.Contains("workspace:  /home/runner/workspaces/proj_x/wr_abc", text);
        Assert.Contains("next action:", text);
        // The new failure kind must NOT reuse the existing delivery-failure
        // labels (dirty-worktree, conflict, base-moved, retry-safe,
        // branch-invariant).
        Assert.DoesNotContain("Conflict needs attention", text);
        Assert.DoesNotContain("Base branch moved", text);
        Assert.DoesNotContain("Transient failure", text);
        Assert.DoesNotContain("Runner / action branch-invariant violation", text);
    }

    [Fact]
    public void DeliveryFailureGuidance_ResolveFailureKindFromMessage_DoesNotMistakeDirtyWorktreeForWorkspaceSetup()
    {
        var (kind, _) = DeliveryFailureGuidance.Resolve(
            "Prepare failed (dirty-worktree): staged changes left behind",
            output: null);
        Assert.Null(kind);
    }

    private static string InvokeTruncate(string value, int softCap)
    {
        var mi = typeof(TableRenderer).GetMethod(
            "Truncate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return (string)mi!.Invoke(null, new object[] { value, softCap })!;
    }

    private static RecordingHandler BuildHandler(string json) =>
        new(HttpStatusCode.OK, json);

    private static MohistCliApi BuildApi(RecordingHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(HttpStatusCode status, string json)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_response);
        }
    }
}
