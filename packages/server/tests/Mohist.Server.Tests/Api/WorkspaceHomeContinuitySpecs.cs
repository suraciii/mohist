using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workspace.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Api;

/// <summary>
/// One WorkflowRun keeps one Server-owned Named Workspace identity, one run
/// branch, and one remote commit lineage as it walks Plan, Build, Check, an
/// Approval Feedback cycle, and Integrate. The plan's bound non-Git artifacts
/// stay visible to later stages, and a no-advancement publishing push leaves
/// the feedback Open while an advancing push resolves it. The Server is the
/// only application started here; Git is the in-memory
/// <see cref="FakeRunnerWorkspaceClient"/>, artifacts live in
/// <see cref="InMemoryWorkflowArtifactStorage"/>, and dispatch is the
/// in-process poll/report loop.
/// </summary>
[Collection("WorkspaceIntegration")]
[Trait("level", "L1")]
public sealed class WorkspaceHomeContinuitySpecs
{
    private const string RepositoryName = "origin";
    private const string GitUrl = "git@example.com:test.git";
    private const string BaseBranch = "main";
    private static readonly string[] PlanArtifactPaths =
        ["PLANS/PLAN.md", "PLANS/DESIGN.md", "PLANS/tasks.json"];

    private readonly IsolatedMohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public WorkspaceHomeContinuitySpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _fixture.RunnerWorkspace.Reset();
    }

    [Fact]
    public async Task OneWorkflowRun_KeepsOneWorkspaceIdentityBranchAndCommitLineageAcrossAllFiveStages()
    {
        var run = await StartContinuityRunAsync("continuity");
        var projectId = run.ProjectId;
        var issueNumber = run.IssueNumber;
        var runnerId = run.RunnerId;
        var runId = run.RunId;
        var workflow = run.Workflow;
        var workspaceGrain = run.Workspace;
        var branch = run.Branch;
        var homePath = run.HomePath;

        // --- Plan: the run's plan artifacts are uploaded and bound at report time. ---
        var plan = await PollAsync(runnerId);
        Assert.Equal("plan", plan.Stage);
        Assert.Equal("spec/task", plan.Uses);
        AssertIdentity(plan, runId, issueNumber);

        var uploadIds = new List<string>();
        foreach (var path in PlanArtifactPaths)
            uploadIds.Add(await UploadArtifactAsync(runId, plan.WorkId, path));

        await ReportAsync(runnerId, plan, "completed", artifactUploadIds: uploadIds.ToArray());

        await AssertWorkspaceStatusAsync(projectId, issueNumber, branch);
        await AssertCommitsAsync(projectId, issueNumber, branch, "commit-a");

        // The remote branch advances between stages; the base stays the same.
        _fixture.RunnerWorkspace.Commits = Commits(branch, "commit-b");

        // --- Build: the bound plan artifacts are provisioned for a later work item. ---
        var build = await PollAsync(runnerId);
        Assert.Equal("build", build.Stage);
        AssertIdentity(build, runId, issueNumber);

        var artifacts = (await GetProvisionedArtifactsAsync(runnerId, runId, build.WorkId))
            .GetProperty("artifacts")
            .EnumerateArray()
            .ToList();
        Assert.Equal(
            PlanArtifactPaths.OrderBy(path => path, StringComparer.Ordinal),
            artifacts.Select(artifact => artifact.GetProperty("path").GetString()!).OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(artifacts, artifact =>
        {
            Assert.False(artifact.TryGetProperty("uploadId", out _));
            Assert.StartsWith("sha256:", artifact.GetProperty("contentHash").GetString());
            Assert.True(artifact.GetProperty("size").GetInt64() > 0);
        });

        await ReportAsync(runnerId, build, "completed");
        await AssertCommitsAsync(projectId, issueNumber, branch, "commit-b");

        // --- Check: the run reaches the Approval Point. ---
        var check = await PollAsync(runnerId);
        Assert.Equal("check", check.Stage);
        AssertIdentity(check, runId, issueNumber);
        await ReportAsync(runnerId, check, "completed");

        var awaiting = await LoadRunAsync(runId);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, awaiting.Status);
        Assert.Equal("check", awaiting.CurrentStageId);

        // --- Approval Feedback: a push that reports no advancement stays Open. ---
        var noAdvanceFeedback = await workflow.RequestChangesAsync("please revise the check", "operator-1");
        var apply = await PollAsync(runnerId);
        Assert.StartsWith("apply-feedback", apply.ActionAttemptId);
        AssertIdentity(apply, runId, issueNumber);
        await ReportAsync(runnerId, apply, "completed");

        var noAdvancePush = await PollAsync(runnerId);
        Assert.StartsWith("publish-feedback", noAdvancePush.ActionAttemptId);
        AssertIdentity(noAdvancePush, runId, issueNumber);
        await ReportAsync(
            runnerId,
            noAdvancePush,
            "completed",
            Output(new { kind = "push", updated = false, landedCommit = "commit-b" }));

        var stillOpen = await LoadRunAsync(runId);
        Assert.Equal(
            ApprovalFeedbackStatus.Open,
            stillOpen.Feedback.Single(feedback => feedback.Id == noAdvanceFeedback).Status);
        Assert.Equal("check", stillOpen.CurrentStageId);

        // Restart the check stage to request approval again. The unresolved
        // request is the observable "no advancement" evidence.
        await workflow.RerunAsync();
        var checkRerun = await PollAsync(runnerId);
        Assert.Equal("check", checkRerun.Stage);
        AssertIdentity(checkRerun, runId, issueNumber);
        await ReportAsync(runnerId, checkRerun, "completed");
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, (await LoadRunAsync(runId)).Status);

        // --- Approval Feedback: an advancing push resolves and returns to the same Approval Point. ---
        var advancingFeedback = await workflow.RequestChangesAsync("apply the check feedback", "operator-1");
        var applyAgain = await PollAsync(runnerId);
        Assert.StartsWith("apply-feedback", applyAgain.ActionAttemptId);
        AssertIdentity(applyAgain, runId, issueNumber);
        await ReportAsync(runnerId, applyAgain, "completed");

        var advancingPush = await PollAsync(runnerId);
        Assert.StartsWith("publish-feedback", advancingPush.ActionAttemptId);
        AssertIdentity(advancingPush, runId, issueNumber);
        await ReportAsync(
            runnerId,
            advancingPush,
            "completed",
            Output(new { kind = "push", updated = true, landedCommit = "commit-1" }));

        var resolved = await LoadRunAsync(runId);
        Assert.Equal(
            ApprovalFeedbackStatus.Resolved,
            resolved.Feedback.Single(feedback => feedback.Id == advancingFeedback).Status);
        Assert.Equal("check", resolved.CurrentStageId);

        var checkAfterResolution = await PollAsync(runnerId);
        Assert.Equal("check", checkAfterResolution.Stage);
        AssertIdentity(checkAfterResolution, runId, issueNumber);
        await ReportAsync(runnerId, checkAfterResolution, "completed");
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, (await LoadRunAsync(runId)).Status);

        await workflow.ApproveAsync("operator-1");

        // --- Integrate: the push's landedCommit is visible to the next dispatch. ---
        var integratePush = await PollAsync(runnerId);
        Assert.Equal("integrate", integratePush.Stage);
        Assert.Equal("mohist/push", integratePush.Uses);
        AssertIdentity(integratePush, runId, issueNumber);
        await ReportAsync(
            runnerId,
            integratePush,
            "completed",
            Output(new { kind = "push", updated = true, landedCommit = "commit-2" }));
        await AssertCommitsAsync(projectId, issueNumber, branch, "commit-2");

        var integrateHealth = await PollAsync(runnerId);
        Assert.Equal("integrate", integrateHealth.Stage);
        AssertIdentity(integrateHealth, runId, issueNumber);
        var landedCommit = Payload(integrateHealth)
            .GetProperty("tasks")
            .GetProperty("integrate:push")
            .GetProperty("outputs")
            .GetProperty("landedCommit")
            .GetString();
        Assert.Equal("commit-2", landedCommit);
        await ReportAsync(runnerId, integrateHealth, "completed");

        var completed = await LoadRunAsync(runId);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(["plan", "build", "check", "integrate"], completed.Stages.Select(stage => stage.Id));
        Assert.Equal(branch, _fixture.RunnerWorkspace.WorkspaceStatus.Branch);
        Assert.Equal(homePath, (await workspaceGrain.GetHomeAsync())!.Path);
    }

    [Fact]
    public async Task HomeLoss_LaterWorkItemStillGetsSameIdentityAndBoundArtifacts()
    {
        var run = await StartContinuityRunAsync("continuity-home-loss");

        var plan = await PollAsync(run.RunnerId);
        Assert.Equal("plan", plan.Stage);
        AssertIdentity(plan, run.RunId, run.IssueNumber);
        await ReportPlanWithArtifactsAsync(run, plan);

        // Home loss is the Runner peer reporting the local Home gone. The
        // Server owns the logical Workspace and the durable bound artifacts.
        _fixture.RunnerWorkspace.WorkspaceStatus = new WorkspaceStatus
        {
            Exists = false,
            Reason = "workspace_removed",
        };
        await AssertWorkspaceStatusLostAsync(run);

        // A later work item still carries the same Git inputs needed to
        // re-clone the Home, and the provisioning route still serves exactly
        // the bound non-Git artifacts from durable storage.
        var build = await PollAsync(run.RunnerId);
        Assert.Equal("build", build.Stage);
        AssertIdentity(build, run.RunId, run.IssueNumber);
        await AssertBoundPlanArtifactsAsync(run, build.WorkId);

        await AssertRunStillActiveAsync(run, "build", build.WorkId);
    }

    [Fact]
    public async Task PendingUpload_IsNotProvisionedAndDoesNotAdvanceTheRun()
    {
        var run = await StartContinuityRunAsync("continuity-pending");

        var plan = await PollAsync(run.RunnerId);
        await ReportPlanWithArtifactsAsync(run, plan);

        var build = await PollAsync(run.RunnerId);
        Assert.Equal("build", build.Stage);

        const string pendingPath = "PLANS/pending.md";
        using (var response = await PostArtifactAsync(run.RunId, build.WorkId, pendingPath, "# pending\n"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(pendingPath, data.GetProperty("path").GetString());
        }

        // The pending upload never becomes a durable provisioning input.
        var artifacts = (await GetProvisionedArtifactsAsync(run.RunnerId, run.RunId, build.WorkId))
            .GetProperty("artifacts")
            .EnumerateArray()
            .ToList();
        Assert.DoesNotContain(artifacts, artifact => artifact.GetProperty("path").GetString() == pendingPath);
        await AssertBoundPlanArtifactsAsync(run, build.WorkId);

        // Uploading is not reporting: the run stays on the same work item.
        await AssertRunStillActiveAsync(run, "build", build.WorkId);
    }

    [Fact]
    public async Task StaleWorkId_ReturnsNotFoundFromUploadAndProvisioningRoutes()
    {
        var run = await StartContinuityRunAsync("continuity-stale");

        var plan = await PollAsync(run.RunnerId);
        await ReportPlanWithArtifactsAsync(run, plan);

        var build = await PollAsync(run.RunnerId);
        Assert.Equal("build", build.Stage);

        // The plan work item is stale now that its stage advanced.
        using (var upload = await PostArtifactAsync(run.RunId, plan.WorkId, "PLANS/PLAN.md", "# stale\n"))
        {
            Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
            var body = await upload.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_found", body.GetProperty("code").GetString());
        }

        using (var provision = await _client.GetAsync(
            $"/api/runner/{run.RunnerId}/workflow-runs/{run.RunId}/work/{plan.WorkId}/workspace-artifacts"))
        {
            Assert.Equal(HttpStatusCode.NotFound, provision.StatusCode);
        }

        await AssertRunStillActiveAsync(run, "build", build.WorkId);
    }

    [Fact]
    public async Task WrongRunner_ReceivesForbiddenOnProvisioningRoute()
    {
        var run = await StartContinuityRunAsync("continuity-wrong-runner");

        var plan = await PollAsync(run.RunnerId);
        await ReportPlanWithArtifactsAsync(run, plan);

        var build = await PollAsync(run.RunnerId);
        Assert.Equal("build", build.Stage);

        using (var response = await _client.GetAsync(
            $"/api/runner/other-runner-{Guid.NewGuid():N}/workflow-runs/{run.RunId}/work/{build.WorkId}/workspace-artifacts"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("workflow_runner_not_assigned", body.GetProperty("code").GetString());
        }

        await AssertRunStillActiveAsync(run, "build", build.WorkId);
    }

    [Fact]
    public async Task HashConflictAtUpload_IsRejectedAndLeavesBoundArtifactUnchanged()
    {
        var run = await StartContinuityRunAsync("continuity-hash-conflict");

        // Plan binds its three declared artifacts, including PLANS/PLAN.md.
        var plan = await PollAsync(run.RunnerId);
        Assert.Equal("plan", plan.Stage);
        await ReportPlanWithArtifactsAsync(run, plan);

        var build = await PollAsync(run.RunnerId);
        Assert.Equal("build", build.Stage);

        const string path = "PLANS/PLAN.md";
        var boundHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"# {path}\n"))).ToLowerInvariant();

        // The build work item uploads the same path twice with different
        // contents; the second upload conflicts with the pending first one.
        string pendingUploadId;
        using (var first = await PostArtifactAsync(run.RunId, build.WorkId, path, "# build plan v1\n"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            pendingUploadId = (await first.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("data")
                .GetProperty("uploadId")
                .GetString()!;
        }

        using (var conflict = await PostArtifactAsync(run.RunId, build.WorkId, path, "# build plan v2\n"))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("artifact_upload_conflict", body.GetProperty("code").GetString());
            var details = body.GetProperty("details");
            Assert.Equal(pendingUploadId, details.GetProperty("existingUploadId").GetString());
            Assert.NotEqual(
                details.GetProperty("existingContentHash").GetString(),
                details.GetProperty("incomingContentHash").GetString());
        }

        // The previously bound Plan artifact still wins: the pending build
        // uploads were never bound and cannot shadow it.
        var artifacts = (await GetProvisionedArtifactsAsync(run.RunnerId, run.RunId, build.WorkId))
            .GetProperty("artifacts")
            .EnumerateArray()
            .ToList();
        var boundPlan = artifacts.Single(artifact => artifact.GetProperty("path").GetString() == path);
        Assert.Equal(boundHash, boundPlan.GetProperty("contentHash").GetString());

        await AssertRunStillActiveAsync(run, "build", build.WorkId);
    }

    private async Task<ContinuityRun> StartContinuityRunAsync(string prefix)
    {
        var projectId = $"{prefix}-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            projectId,
            new RepositoryInfo
            {
                Name = RepositoryName,
                GitUrl = GitUrl,
                BaseBranch = BaseBranch,
                IsDefault = true,
            },
            "true");

        var issueNumber = await _fixture.Grains
            .GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId))
            .NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.CreateAsync(projectId, issueNumber, "continuity issue", null, null, null, isDraft: false);

        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(
            _fixture.ConnectionString,
            projectId,
            ContinuityDefinition());

        var runnerId = $"{prefix}-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            new RunnerInfo(
                runnerId,
                ["spec/*", AgentExecutionSources.Version1Capability],
                "test-host",
                projectId,
                ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration,
                RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()),
            TestRunnerGenerationExtensions.ProcessGeneration);

        var runId = await issueGrain.StartWorkAsync();
        await DispatchEventsAsync();

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(runId);
        await workflow.AssignWorkerAsync(runnerId);

        var workspaceName = $"issue-{issueNumber}";
        var branch = $"mohist/ws-{workspaceName}";
        var homePath = $"/mohist-tests/runner/{workspaceName}";
        var workspace = _fixture.Grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, workspaceName));
        Assert.NotNull(await workspace.EnsureMaterializedOnAsync(
            runnerId,
            homePath,
            _fixture.TimeProvider.GetUtcNow()));

        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(branch);
        _fixture.RunnerWorkspace.Commits = Commits(branch, "commit-a");

        return new ContinuityRun(
            projectId,
            issueNumber,
            runnerId,
            runId,
            workflow,
            workspace,
            workspaceName,
            branch,
            homePath);
    }

    private async Task ReportPlanWithArtifactsAsync(ContinuityRun run, WorkDispatch plan)
    {
        var uploadIds = new List<string>();
        foreach (var path in PlanArtifactPaths)
            uploadIds.Add(await UploadArtifactAsync(run.RunId, plan.WorkId, path));
        await ReportAsync(run.RunnerId, plan, "completed", artifactUploadIds: uploadIds.ToArray());
    }

    private async Task AssertBoundPlanArtifactsAsync(ContinuityRun run, string workId)
    {
        var artifacts = (await GetProvisionedArtifactsAsync(run.RunnerId, run.RunId, workId))
            .GetProperty("artifacts")
            .EnumerateArray()
            .ToList();
        Assert.Equal(
            PlanArtifactPaths.OrderBy(path => path, StringComparer.Ordinal),
            artifacts.Select(artifact => artifact.GetProperty("path").GetString()!).OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(artifacts, artifact =>
        {
            Assert.False(artifact.TryGetProperty("uploadId", out _));
            Assert.StartsWith("sha256:", artifact.GetProperty("contentHash").GetString());
            Assert.True(artifact.GetProperty("size").GetInt64() > 0);
        });
    }

    private async Task AssertWorkspaceStatusLostAsync(ContinuityRun run)
    {
        var status = await _client.GetDataAsync<StatusDto>(
            $"/api/projects/{run.ProjectId}/issues/{run.IssueNumber}/workspace-status");
        Assert.False(status.Exists);
        Assert.Equal("workspace_removed", status.Reason);
    }

    private async Task AssertRunStillActiveAsync(ContinuityRun run, string stage, string workId)
    {
        var current = await LoadRunAsync(run.RunId);
        Assert.False(current.Status.IsTerminal());
        Assert.Equal(stage, current.CurrentStageId);
        var active = await run.Workflow.GetActiveWorkAsync(workId);
        Assert.NotNull(active);
    }

    private async Task<HttpResponseMessage> PostArtifactAsync(string runId, string workId, string path, string body)
    {
        var content = Encoding.UTF8.GetBytes(body);
        var contentHash = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var form = BuildMultipart(path, content, "text/markdown", contentHash, content.LongLength);
        return await _client.PostAsync($"/api/workflow-runs/{runId}/work/{workId}/artifact-uploads", form);
    }

    private static WorkflowDefinition ContinuityDefinition() => new(
    [
        new StageDefinition(
            "plan",
            [new TaskDefinition(
                "plan",
                "Plan the change",
                "spec/task",
                Artifacts: new TaskArtifactCapture([
                    new TaskArtifactDeclaration("PLANS/PLAN.md"),
                    new TaskArtifactDeclaration("PLANS/DESIGN.md"),
                    new TaskArtifactDeclaration("PLANS/tasks.json"),
                ]))],
            []),
        new StageDefinition(
            "build",
            [new TaskDefinition("build", "Build the change", "spec/task")],
            []),
        new StageDefinition(
            "check",
            [new TaskDefinition("check", "Check the change", "spec/task")],
            [],
            RequiresApproval: true),
        new StageDefinition(
            "integrate",
            [
                new TaskDefinition("integrate:push", "Push changes", "mohist/push"),
                new TaskDefinition("integrate:health", "Integrate health", "spec/task"),
            ],
            []),
    ],
    Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
        new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/task"),
        new TaskDefinition("publish-feedback", "Publish approval feedback", "mohist/push"),
    ])));

    private static WorkspaceStatus AvailableStatus(string branch) => new()
    {
        Exists = true,
        Branch = branch,
        BaseBranch = BaseBranch,
        Ahead = 0,
        Behind = 0,
        RebaseInProgress = false,
        ConflictingFiles = [],
    };

    private static RunnerWorkspaceCommitsResult Commits(string branch, string head) => new(
        BaseBranch,
        branch,
        "merge-base",
        head == "commit-a" ? 1 : 2,
        0,
        1,
        4,
        2,
        [new GitCommit(head, head, "Commit", "Author", "2026-01-01T00:00:00Z", [])]);

    private async Task<WorkDispatch> PollAsync(string runnerId)
    {
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var dispatch = await runner.PollAsync(_fixture.Services);
        Assert.NotNull(dispatch);
        return dispatch!;
    }

    private async Task ReportAsync(
        string runnerId,
        WorkDispatch work,
        string status,
        JsonElement? Output = null,
        string[]? artifactUploadIds = null)
    {
        await DispatchTestExtensions.ReportWorkflowDirectAsync(
            _fixture.Grains,
            _fixture.Services,
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            new WorkResult(status, Output: Output, ArtifactUploadIds: artifactUploadIds));
    }

    private async Task<string> UploadArtifactAsync(string runId, string workId, string path)
    {
        var content = Encoding.UTF8.GetBytes($"# {path}\n");
        var contentHash = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var form = BuildMultipart(path, content, "text/markdown", contentHash, content.LongLength);
        using var response = await _client.PostAsync(
            $"/api/workflow-runs/{runId}/work/{workId}/artifact-uploads",
            form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return data.GetProperty("uploadId").GetString()!;
    }

    private async Task<JsonElement> GetProvisionedArtifactsAsync(string runnerId, string runId, string workId)
    {
        using var response = await _client.GetAsync(
            $"/api/runner/{runnerId}/workflow-runs/{runId}/work/{workId}/workspace-artifacts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private async Task AssertWorkspaceStatusAsync(string projectId, int issueNumber, string branch)
    {
        var status = await _client.GetDataAsync<StatusDto>(
            $"/api/projects/{projectId}/issues/{issueNumber}/workspace-status");
        Assert.True(status.Exists);
        Assert.Equal(branch, status.Branch);
        Assert.Equal(BaseBranch, status.BaseBranch);
    }

    private async Task AssertCommitsAsync(string projectId, int issueNumber, string branch, string head)
    {
        _fixture.RunnerWorkspace.Commits = Commits(branch, head);
        var commits = await _client.GetDataAsync<CommitsDto>(
            $"/api/projects/{projectId}/issues/{issueNumber}/commits");
        Assert.True(commits.Available);
        Assert.Equal(BaseBranch, commits.Base);
        Assert.Equal(branch, commits.Head);
        Assert.Contains(commits.Commits, commit => commit.Hash == head);
    }

    private async Task<WorkflowRun> LoadRunAsync(string runId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(runId)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found");
    }

    private static void AssertIdentity(WorkDispatch work, string runId, int issueNumber)
    {
        var payload = Payload(work);
        Assert.Equal(runId, payload.GetProperty("workflow").GetProperty("runId").GetString());
        var workspace = payload.GetProperty("workspace");
        Assert.Equal($"issue-{issueNumber}", workspace.GetProperty("name").GetString());
        Assert.Equal($"mohist/ws-issue-{issueNumber}", workspace.GetProperty("branch").GetString());
        var repository = payload.GetProperty("repository");
        Assert.Equal(RepositoryName, repository.GetProperty("name").GetString());
        Assert.Equal(GitUrl, repository.GetProperty("gitUrl").GetString());
        Assert.Equal(BaseBranch, repository.GetProperty("baseBranch").GetString());
    }

    private static JsonElement Payload(WorkDispatch work)
    {
        Assert.False(string.IsNullOrWhiteSpace(work.Variables));
        return JsonDocument.Parse(work.Variables!).RootElement.Clone();
    }

    private static JsonElement Output(object value) => JsonSerializer.SerializeToElement(value);

    private static MultipartFormDataContent BuildMultipart(
        string path,
        byte[] content,
        string contentType,
        string contentHash,
        long size)
    {
        var form = new MultipartFormDataContent("----mohist-test-" + Guid.NewGuid().ToString("N"));
        form.Add(new StringContent(path), "path");
        form.Add(new StringContent(contentType), "contentType");
        form.Add(new StringContent(contentHash), "contentHash");
        form.Add(new StringContent(size.ToString()), "size");
        var stream = new ByteArrayContent(content);
        stream.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(stream, "content", "artifact.bin");
        return form;
    }

    private Task DispatchEventsAsync() =>
        _fixture.Services.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventDispatcher>().DrainAsync();

    private sealed record ContinuityRun(
        string ProjectId,
        int IssueNumber,
        string RunnerId,
        string RunId,
        IWorkflowGrain Workflow,
        IWorkspaceGrain Workspace,
        string WorkspaceName,
        string Branch,
        string HomePath);

    private sealed record StatusDto(
        bool Exists,
        string? Reason,
        string? Branch,
        string? BaseBranch,
        int Ahead,
        int Behind,
        bool RebaseInProgress,
        string[] ConflictingFiles);

    private sealed record CommitsDto(
        bool Available,
        string? Reason,
        string? Message,
        string Base,
        string Head,
        string MergeBase,
        int Ahead,
        int Behind,
        bool CanFastForward,
        string Comparison,
        SummaryDto Summary,
        GitCommitDto[] Commits);

    private sealed record SummaryDto(int FilesChanged, int Commits, int Additions, int Deletions);

    private sealed record GitCommitDto(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);
}
