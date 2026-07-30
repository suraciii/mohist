using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentJobRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentJobRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostValidate_MissingPrompt_ReturnsValidationError_AndDoesNotCreateGrain()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                model = "openai/gpt-4",
                agentId = "agent-validation",
                workspace = new { path = "/tmp/agent-job-validation", projectId = "validation-project" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("validation_failed", payload.GetProperty("code").GetString());
        Assert.Contains("prompt", payload.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task PostValidate_WorkspacePathMissing_ReturnsValidationError()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "hello",
                agentId = "agent-validation",
                workspace = new { projectId = "validation-project" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("workspace.path", payload.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task PostValidate_MissingAgentId_ReturnsValidationError()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "hello",
                workspace = new { path = "/tmp/agent-job-validation", projectId = "validation-project" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("validation_failed", payload.GetProperty("code").GetString());
        Assert.Contains("agentId", payload.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task PostValidate_NoRunnerAvailable_ReturnsStructuredFailureWithRunnerUnavailableReason()
    {
        var grain = new TerminalAgentJobGrain(new AgentJobTerminalResult(
            AgentJobStatus.Failed,
            AgentJobFailureReasons.RunnerUnavailable,
            null,
            null,
            AgentJobFailureReasons.RunnerUnavailable,
            null));
        var request = AgentJobRouteTestHelpers.JsonRequest(new
        {
            prompt = "do the thing without a runner",
            agentId = "agent-validation",
            model = "test/model",
            workspace = new { path = "/tmp/agent-job-no-runner", projectId = "validation-no-runner-project" },
        });

        var result = await AgentJobController.HandleValidateAsync(
            request,
            new SingleAgentJobGrainFactory(grain),
            Options.Create(new AgentJobOptions { JobTimeout = TimeSpan.FromSeconds(8) }),
            new FakeTimeProvider(TestTime.UtcNow),
            CancellationToken.None);

        var payload = await AgentJobRouteTestHelpers.ExecuteJsonResultAsync(result);
        Assert.True(payload.GetProperty("success").GetBoolean());
        var data = payload.GetProperty("data");
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, data.GetProperty("failureReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("jobId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task PostValidate_WhenJobTimesOut_ReturnsStructuredTimeoutResult_NotOpaque500()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var grain = new PendingAgentJobGrain();
        var request = AgentJobRouteTestHelpers.JsonRequest(new
        {
            prompt = "never reports back",
            agentId = "agent-validation",
            model = "test/model",
            workspace = new { path = "/tmp/agent-job-timeout", projectId = "validation-timeout-project" },
        });

        var responseTask = AgentJobController.HandleValidateAsync(
            request,
            new SingleAgentJobGrainFactory(grain),
            Options.Create(new AgentJobOptions { JobTimeout = TimeSpan.FromSeconds(8) }),
            time,
            CancellationToken.None);

        await grain.Submitted;
        await grain.TerminalWaitStarted;
        Assert.False(responseTask.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(38));

        var result = await responseTask;
        var payload = await AgentJobRouteTestHelpers.ExecuteJsonResultAsync(result);

        Assert.True(payload.GetProperty("success").GetBoolean());
        var data = payload.GetProperty("data");
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal("timeout", data.GetProperty("failureReason").GetString());
        Assert.Equal(1, grain.SubmitCount);
    }

}

internal static class AgentJobRouteTestHelpers
{
    public static HttpRequest JsonRequest(object body)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JSON.Serialize(body)));
        request.ContentLength = request.Body.Length;
        request.ContentType = "application/json";
        return request;
    }

    public static async Task<JsonElement> ExecuteJsonResultAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                {
                    o.SerializerOptions.Converters.Add(converter);
                }
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
    }
}

internal sealed class TerminalAgentJobGrain : IAgentJobGrain
{
    private readonly AgentJobTerminalResult _result;

    public TerminalAgentJobGrain(AgentJobTerminalResult result)
    {
        _result = result;
    }

    public Task<bool> IsWorkRunnableAsync(string runnerId, string workId) => Task.FromResult(false);
    public Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result) => Task.FromResult(new AgentJobReportResult(false, "already-terminal"));
    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(_result.Status);
    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult<string?>(null);
    public Task<ClaimResult?> ClaimNextAsync(string runnerId) => Task.FromResult<ClaimResult?>(null);
    public Task AssignRunnerAsync(string runnerId, string workId) => Task.CompletedTask;
    public Task SubmitAsync(AgentJobInput input) => Task.CompletedTask;
    public Task EnsureSubmittedAsync(AgentJobInput input) => Task.CompletedTask;
    public Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan) => Task.FromResult(plan);
    public Task AdvancePreparedLaunchAsync() => Task.CompletedTask;
    public Task CheckTimeoutsAsync() => Task.CompletedTask;
    public Task<AgentJobTerminalResult> GetTerminalResultAsync() => Task.FromResult(_result);
    public Task<AgentJobTerminalResult> WaitForTerminalAsync() => Task.FromResult(_result);
    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() => Task.FromResult(new AgentJobRuntimeSnapshot(_result.Status, null, null, _result.FailureReason));
    public Task FailAsync(string reason, string? agentId = null) => Task.CompletedTask;
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}

internal sealed class PendingAgentJobGrain : IAgentJobGrain
{
    private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminalWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AgentJobTerminalResult> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int SubmitCount { get; private set; }
    public Task Submitted => _submitted.Task;
    public Task TerminalWaitStarted => _terminalWaitStarted.Task;
    private string? _failureReason;

    public Task<bool> IsWorkRunnableAsync(string runnerId, string workId) => Task.FromResult(false);
    public Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result) => Task.FromResult(new AgentJobReportResult(false, "not-running"));
    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(_failureReason is null ? AgentJobStatus.Pending : AgentJobStatus.Failed);
    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult<string?>(null);
    public Task<ClaimResult?> ClaimNextAsync(string runnerId) => Task.FromResult<ClaimResult?>(null);
    public Task AssignRunnerAsync(string runnerId, string workId) => Task.CompletedTask;
    public Task SubmitAsync(AgentJobInput input)
    {
        SubmitCount++;
        _submitted.TrySetResult();
        return Task.CompletedTask;
    }
    public Task EnsureSubmittedAsync(AgentJobInput input) => SubmitAsync(input);
    public Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan) => Task.FromResult(plan);
    public Task AdvancePreparedLaunchAsync() => Task.CompletedTask;
    public Task CheckTimeoutsAsync() => Task.CompletedTask;
    public Task<AgentJobTerminalResult> GetTerminalResultAsync() => Task.FromResult(new AgentJobTerminalResult(_failureReason is null ? AgentJobStatus.Pending : AgentJobStatus.Failed, _failureReason, null, null, _failureReason, null));
    public Task<AgentJobTerminalResult> WaitForTerminalAsync() { _terminalWaitStarted.TrySetResult(); return _terminal.Task; }
    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() => Task.FromResult(new AgentJobRuntimeSnapshot(_failureReason is null ? AgentJobStatus.Pending : AgentJobStatus.Failed, null, null, _failureReason));
    public Task FailAsync(string reason, string? agentId = null)
    {
        _failureReason = reason;
        return Task.CompletedTask;
    }
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}

internal sealed class SingleAgentJobGrainFactory : IGrainFactory
{
    private readonly IAgentJobGrain _grain;

    public SingleAgentJobGrainFactory(IAgentJobGrain grain)
    {
        _grain = grain;
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();

    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();

    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey
    {
        if (typeof(TGrainInterface) == typeof(IAgentJobGrain))
            return (TGrainInterface)_grain;
        throw new NotSupportedException(typeof(TGrainInterface).FullName);
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();

    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();

    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
    {
        if (grainInterfaceType == typeof(IAgentJobGrain))
            return _grain;
        throw new NotSupportedException(grainInterfaceType.FullName);
    }
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
        where TGrainInterface : IAddressable => throw new NotSupportedException();
    public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
    public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
}

[Collection("MohistIntegration")]
public class AgentJobDispatchRouteSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentJobDispatchRouteSpecs(MohistIntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task PostValidate_DispatchesAgentJobToRunner_AndReturnsReportedCompletion()
    {
        var projectId = await CreateProjectAsync("agent-route-project");
        var agentId = (await CreateAgentAsync(projectId, "validation-agent")).Id;
        var runnerId = $"agent-route-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-route-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 2);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "route completion prompt",
                    agentId,
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-route", projectId },
                });

            var workId = await WaitForAgentJobDispatchAsync(jobKey, runnerId);
            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
            await jobGrain.ReportResultAsync(
                runnerId,
                workId,
                new WorkResult("completed", "ok", JSON.DeserializeElement("{\"hello\":\"world\"}"), 0, ["artifact-a"]));

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

            var snapshot = await _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey).GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Completed, snapshot.Status);
            Assert.Equal(runnerId, snapshot.RunnerId);

            var finalState = await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync();
            Assert.DoesNotContain(jobKey, finalState.ActiveWorks.Select(w => w.OwnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task PostValidate_WhenRunnerReportsFailure_ReturnsStructuredFailure()
    {
        var projectId = await CreateProjectAsync("agent-route-fail-project");
        var agentId = (await CreateAgentAsync(projectId, "validation-agent")).Id;
        var runnerId = $"agent-route-fail-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-route-fail-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 4);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "do the failing thing",
                    agentId,
                    model = "test/model",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-fail", projectId },
                });

            var workId = await WaitForAgentJobDispatchAsync(jobKey, runnerId);
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey).ReportResultAsync(
                runnerId,
                workId,
                new WorkResult("failed", "runner reported failure", JSON.DeserializeElement("{\"error\":\"x\"}"), 1));

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

    [Fact]
    public async Task RunnerReportEndpoint_ForAgentJob_DeliversResultToValidateResponse()
    {
        var projectId = await CreateProjectAsync("agent-route-http-report-project");
        var agentId = (await CreateAgentAsync(projectId, "validation-agent")).Id;
        var runnerId = $"agent-route-http-report-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-route-http-report-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 2);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "http report route prompt",
                    agentId,
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-http-report", projectId },
                });

            var workId = await WaitForAgentJobDispatchAsync(jobKey, runnerId);

            var reportResponse = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/report",
                new
                {
                    workId,
                    status = "completed",
                    ownerKind = WorkDispatchOwnerKinds.AgentJob,
                    agentJobId = jobKey,
                    message = "ok from http",
                    output = new { hello = "http" },
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

    [Fact]
    public async Task RunnerPollEndpoint_ForAgentJob_ExposesOwnerKindAndAgentJobId()
    {
        var projectId = await CreateProjectAsync("agent-route-http-poll-project");
        var agentId = (await CreateAgentAsync(projectId, "validation-agent")).Id;
        var runnerId = $"agent-route-http-poll-runner-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-validate-route-http-poll-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId, maxWorkflowSlots: 1);

        try
        {
            var responseTask = _fixture.Client.PostAsJsonAsync(
                AgentJobController.ValidatePath,
                new
                {
                    prompt = "http poll route prompt",
                    agentId,
                    model = "openai/gpt-test",
                    jobId = jobKey,
                    workspace = new { path = "/tmp/agent-job-http-poll", projectId },
                });

            var workId = await WaitForAgentJobDispatchAsync(jobKey, runnerId);

            using var httpResponse = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var httpBody = await httpResponse.ReadFirstDispatchElementAsync()
                ?? throw new InvalidOperationException("Expected a dispatch from /poll");
            Assert.Equal(workId, httpBody.GetProperty("workId").GetString());
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, httpBody.GetProperty("ownerKind").GetString());
            Assert.Equal(jobKey, httpBody.GetProperty("agentJobId").GetString());

            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey).ReportResultAsync(
                runnerId,
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

    [Fact]
    public async Task HttpReportEndpoint_AgentJobWithoutAgentJobId_Returns400()
    {
        var projectId = $"agent-route-http-bad-request-project-{Guid.NewGuid():N}";
        var runnerId = $"agent-route-http-bad-request-runner-{Guid.NewGuid():N}";
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
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = maxWorkflowSlots });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await TestWait.ForAsync(
            () => runnerGrain.GetRuntimeStateAsync(),
            s => s.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"Runner '{runnerId}' to reach Online");
    }

    private async Task<string> WaitForAgentJobDispatchAsync(string agentJobId, string expectedRunnerId)
    {
        var assignment = await _fixture.AgentJobDispatches.WaitForRunnerAcceptedAsync(agentJobId);
        Assert.Equal(expectedRunnerId, assignment.RunnerId);
        return assignment.WorkId;
    }
}
