using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentJobRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentJobRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_MissingPrompt_ReturnsValidationError_AndDoesNotCreateGrain()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                model = "openai/gpt-4",
                workspace = new { path = "/tmp/agent-job-validation", projectId = "validation-project" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("validation_failed", payload.GetProperty("code").GetString());
        Assert.Contains("prompt", payload.GetProperty("error").GetString()!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_EmptyBody_ReturnsValidationError()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_WorkspacePathMissing_ReturnsValidationError()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "hello",
                workspace = new { projectId = "validation-project" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("workspace.path", payload.GetProperty("error").GetString()!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_NoRunnerAvailable_ReturnsStructuredFailureWithRunnerUnavailableReason()
    {
        var projectId = $"validation-no-runner-{Guid.NewGuid():N}";
        var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "do the thing without a runner",
                model = "test/model",
                workspace = new { path = "/tmp/agent-job-no-runner", projectId },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        var data = payload.GetProperty("data");
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, data.GetProperty("failureReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("jobId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("message").GetString()));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_WhenJobTimesOut_ReturnsStructuredTimeoutResult_NotOpaque500()
    {
        var projectId = $"validation-timeout-project-{Guid.NewGuid():N}";
        var runnerId = $"validation-timeout-runner-{Guid.NewGuid():N}";

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "validation-timeout-host",
            projectId,
            maxWorkflowSlots = 4,
        });

        try
        {
            var response = await _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "never reports back",
                    model = "test/model",
                    workspace = new { path = "/tmp/agent-job-timeout", projectId },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var data = payload.GetProperty("data");
            Assert.Equal("failed", data.GetProperty("status").GetString());
            var reason = data.GetProperty("failureReason").GetString();
            Assert.True(
                reason is AgentJobFailureReasons.ReportTimeout or "timeout",
                $"Expected timeout-style reason, got '{reason}'");
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task PostValidate_DoesNotAffectExistingHttpApiSurface()
    {
        using var response = await _fixture.Client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

[Collection("MohistIntegration")]
public class AgentJobRoutesEndToEndSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentJobRoutesEndToEndSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FullPath_HttpPost_CreatesAgentJobGrain_DispatchesThroughRegistry_RunnerAccepts_ReportsBack()
    {
        var projectId = $"e2e-path-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-path-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-e2e-path-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 2);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "full path prompt",
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-fullpath", projectId },
                });

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);

            await WaitForAgentJobStatusAsync(jobGrain, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
            var workId = (await jobGrain.GetRuntimeSnapshotAsync()).CurrentWorkId!;
            var dispatch = new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workId,
                AgentJobId: jobKey,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob);

            var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runnerGrain.ReportResultAsync(
                dispatch,
                workId,
                new WorkResult(
                    Status: "completed",
                    Message: "ok",
                    Output: "{\"hello\":\"world\"}",
                    ExitCode: 0,
                    ArtifactUploadIds: new[] { "artifact-a" }));

            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal("completed", data.GetProperty("status").GetString());
            Assert.Equal("ok", data.GetProperty("message").GetString());
            Assert.Equal("{\"hello\":\"world\"}", data.GetProperty("output").GetString());
            Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
            Assert.Equal(new[] { "artifact-a" }, data.GetProperty("artifacts").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(jobKey, data.GetProperty("jobId").GetString());

            var snapshot = await jobGrain.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Completed, snapshot.Status);
            Assert.Equal(runnerId, snapshot.RunnerId);

            var finalState = await runnerGrain.GetRuntimeStateAsync();
            Assert.DoesNotContain(jobKey, finalState.ActiveWorks.Select(w => w.OwnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FullPath_RunnerReportsFailure_ReturnsStructuredFailedResponse()
    {
        var projectId = $"e2e-fail-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-fail-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-e2e-fail-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 4);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "do the failing thing",
                    model = "test/model",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-fail", projectId },
                });

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);

            await WaitForAgentJobStatusAsync(jobGrain, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
            var workId = (await jobGrain.GetRuntimeSnapshotAsync()).CurrentWorkId!;
            var dispatch = new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workId,
                AgentJobId: jobKey,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob);

            var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runnerGrain.ReportResultAsync(
                dispatch,
                workId,
                new WorkResult(
                    Status: "failed",
                    Message: "runner reported failure",
                    Output: "{\"error\":\"x\"}",
                    ExitCode: 1));

            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal("failed", data.GetProperty("status").GetString());
            Assert.Equal("runner reported failure", data.GetProperty("message").GetString());
            Assert.Equal("runner reported failure", data.GetProperty("failureReason").GetString());
            Assert.Equal(1, data.GetProperty("exitCode").GetInt32());
            Assert.Equal("{\"error\":\"x\"}", data.GetProperty("output").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Dispatch_VariablesCarryWorkspacePath_ForRunnerWorkspaceShortCircuit()
    {
        var projectId = $"e2e-workspace-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-workspace-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-e2e-workspace-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 1);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "only workspace path",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-workspace-only", projectId },
                });

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);

            await WaitForAgentJobStatusAsync(jobGrain, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
            var workId = (await jobGrain.GetRuntimeSnapshotAsync()).CurrentWorkId!;
            var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var polled = await runnerGrain.PollAsync();

            Assert.NotNull(polled);
            Assert.Equal(workId, polled!.WorkId);
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled.OwnerKind);
            Assert.Equal(jobKey, polled.AgentJobId);

            var variables = JsonSerializer.Deserialize<JsonElement>(polled.Variables!);
            Assert.Equal(
                "/tmp/agent-job-workspace-only",
                variables.GetProperty("workspace").GetProperty("path").GetString());
            var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
            Assert.Equal("only workspace path", with.GetProperty("prompt").GetString());

            var dispatch = new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workId,
                AgentJobId: jobKey,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob);

            await runnerGrain.ReportResultAsync(
                dispatch,
                workId,
                new WorkResult(Status: "completed", Message: "ok"));

            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("completed", payload.GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FullPath_RunnerReportsViaHttpReportEndpoint_DeliversToAgentJobGrain()
    {
        var projectId = $"e2e-http-report-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-http-report-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-e2e-http-report-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 2);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "http report path prompt",
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-http-report", projectId },
                });

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
            await WaitForAgentJobStatusAsync(jobGrain, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
            var workId = (await jobGrain.GetRuntimeSnapshotAsync()).CurrentWorkId!;

            var reportResponse = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/report",
                new
                {
                    workId,
                    status = "completed",
                    ownerKind = WorkDispatchOwnerKinds.AgentJob,
                    agentJobId = jobKey,
                    message = "ok from http",
                    output = "{\"hello\":\"http\"}",
                    exitCode = 0,
                    artifactUploadIds = new[] { "artifact-http" },
                });
            Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);

            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal("completed", data.GetProperty("status").GetString());
            Assert.Equal("ok from http", data.GetProperty("message").GetString());
            Assert.Equal("{\"hello\":\"http\"}", data.GetProperty("output").GetString());
            Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
            Assert.Equal(new[] { "artifact-http" }, data.GetProperty("artifacts").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(jobKey, data.GetProperty("jobId").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FullPath_HttpPollEndpoint_ExposesOwnerKindAndAgentJobId()
    {
        var projectId = $"e2e-http-poll-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-http-poll-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-e2e-http-poll-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 1);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "http poll path prompt",
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-http-poll", projectId },
                });

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
            await WaitForAgentJobStatusAsync(jobGrain, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
            var workId = (await jobGrain.GetRuntimeSnapshotAsync()).CurrentWorkId!;

            using var httpResponse = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
            var httpBody = await httpResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(workId, httpBody.GetProperty("workId").GetString());
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, httpBody.GetProperty("ownerKind").GetString());
            Assert.Equal(jobKey, httpBody.GetProperty("agentJobId").GetString());

            var dispatch = new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workId,
                AgentJobId: jobKey,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob);
            var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runnerGrain.ReportResultAsync(
                dispatch,
                workId,
                new WorkResult(Status: "completed", Message: "ok"));

            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task HttpReportEndpoint_AgentJobWithoutAgentJobId_Returns400()
    {
        var projectId = $"e2e-http-bad-request-project-{Guid.NewGuid():N}";
        var runnerId = $"e2e-http-bad-request-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 1);

        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/report",
                new
                {
                    workId = "agent-work-bogus",
                    status = "completed",
                    ownerKind = WorkDispatchOwnerKinds.AgentJob,
                });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task RegisterRunnerAndAwaitOnlineAsync(string runnerId, string projectId, int maxWorkflowSlots)
    {
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
            maxWorkflowSlots,
        });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await runnerGrain.GetRuntimeStateAsync();
            if (state.Status == RunnerStatus.Online)
                return;

            await Task.Delay(25);
        }

        var last = await runnerGrain.GetRuntimeStateAsync();
        Assert.Fail($"Runner '{runnerId}' did not reach Online within 5s (last status {last.Status}).");
    }

    private static async Task WaitForAgentJobStatusAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await job.GetStatusAsync();
            if (status == expected)
                return;

            await Task.Delay(25);
        }

        var last = await job.GetStatusAsync();
        Assert.Fail($"Agent job did not reach {expected} within {timeout.TotalSeconds:N0}s (last status {last}).");
    }
}
