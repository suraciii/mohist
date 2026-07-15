using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class WorkflowArtifactUploadRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public WorkflowArtifactUploadRouteSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private static string UniqueProjectName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 32, 63)];

    private static MultipartFormDataContent BuildMultipart(string path, byte[] content, string contentType,
        string? contentHash, long size)
    {
        var form = new MultipartFormDataContent("----mohist-test-" + Guid.NewGuid().ToString("N"));
        form.Add(new StringContent(path), "path");
        form.Add(new StringContent(contentType), "contentType");
        if (contentHash is not null)
            form.Add(new StringContent(contentHash), "contentHash");
        form.Add(new StringContent(size.ToString()), "size");
        var stream = new ByteArrayContent(content);
        stream.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(stream, "content", "review.md");
        return form;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_AcceptsMultipartAndReturnsUploadId()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var path = "review.md";
            var payload = Encoding.UTF8.GetBytes("the actual review content");
            using var form = BuildMultipart(path, payload, "text/markdown", "sha256:hash1", payload.LongLength);

            using var response = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");
            var uploadId = data.GetProperty("uploadId").GetString()!;
            Assert.StartsWith("artup_", uploadId);
            Assert.Equal(workflowRunId, data.GetProperty("workflowRunId").GetString());
            Assert.Equal(workId, data.GetProperty("workId").GetString());
            Assert.Equal(path, data.GetProperty("path").GetString());
            Assert.Equal("text/markdown", data.GetProperty("contentType").GetString());
            Assert.Equal("sha256:hash1", data.GetProperty("contentHash").GetString());
            Assert.Equal(payload.LongLength, data.GetProperty("size").GetInt64());
            Assert.False(data.GetProperty("idempotent").GetBoolean());

            // Pending upload row exists, but no bound artifact yet.
            await using var scope = _fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var pending = await db.WorkflowArtifactPendingUploads
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UploadId == uploadId);
            Assert.NotNull(pending);
            Assert.Equal(path, pending!.Path);
            Assert.Equal("text/markdown", pending.ContentType);
            Assert.Equal("sha256:hash1", pending.ContentHash);
            Assert.Equal(payload.LongLength, pending.Size);
            Assert.False(string.IsNullOrEmpty(pending.StoragePath));

            var bound = await db.WorkflowArtifacts
                .AsNoTracking()
                .Where(a => a.WorkflowRunId == workflowRunId)
                .ToListAsync();
            Assert.Empty(bound);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_SameKeySameHashIsIdempotent()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var path = "review.md";
            var payload = Encoding.UTF8.GetBytes("identical content");

            using (var form = BuildMultipart(path, payload, "text/markdown", "sha256:stable", payload.LongLength))
            {
                using var first = await _fixture.Client.PostAsync(
                    $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                    form);
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
                var firstId = firstJson.GetProperty("data").GetProperty("uploadId").GetString();
                Assert.False(firstJson.GetProperty("data").GetProperty("idempotent").GetBoolean());
            }

            using (var form = BuildMultipart(path, payload, "text/markdown", "sha256:stable", payload.LongLength))
            {
                using var second = await _fixture.Client.PostAsync(
                    $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                    form);
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
                var secondId = secondJson.GetProperty("data").GetProperty("uploadId").GetString();
                Assert.True(secondJson.GetProperty("data").GetProperty("idempotent").GetBoolean());
                // Same id is returned, no second row created.
                await using var scope = _fixture.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
                var rows = await db.WorkflowArtifactPendingUploads
                    .AsNoTracking()
                    .Where(p => p.WorkflowRunId == workflowRunId)
                    .ToListAsync();
                Assert.Single(rows);
                Assert.Equal(secondId, rows[0].UploadId);
            }
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_SameKeyDifferentHashReturnsConflict()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var path = "review.md";
            var payload = Encoding.UTF8.GetBytes("first");

            using (var form = BuildMultipart(path, payload, "text/markdown", "sha256:aaa", payload.LongLength))
            {
                using var first = await _fixture.Client.PostAsync(
                    $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                    form);
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            var secondPayload = Encoding.UTF8.GetBytes("second");
            using (var form = BuildMultipart(path, secondPayload, "text/markdown", "sha256:bbb", secondPayload.LongLength))
            {
                using var conflict = await _fixture.Client.PostAsync(
                    $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                    form);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("artifact_upload_conflict", body.GetProperty("code").GetString());
                var details = body.GetProperty("details");
                Assert.Equal("sha256:aaa", details.GetProperty("existingContentHash").GetString());
                Assert.Equal("sha256:bbb", details.GetProperty("incomingContentHash").GetString());
            }

            // Original content is preserved.
            await using var scope = _fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var row = await db.WorkflowArtifactPendingUploads
                .AsNoTracking()
                .FirstAsync(p => p.WorkflowRunId == workflowRunId);
            Assert.Equal("sha256:aaa", row.ContentHash);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_UnknownWorkItemReturnsNotFound()
    {
        var form = BuildMultipart("review.md", new byte[] { 0x01, 0x02 }, "text/markdown", "sha256:zzz", 2);
        using var response = await _fixture.Client.PostAsync(
            $"/api/workflow-runs/wr_does_not_exist/work/task-1.1/artifact-uploads",
            form);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_NonMultipartReturnsBadRequest()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/api/workflow-runs/wr_anything/work/task-1.1/artifact-uploads",
            content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_MissingPathFieldReturnsBadRequest()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var form = new MultipartFormDataContent("----mohist-test-" + Guid.NewGuid().ToString("N"));
            var bytes = Encoding.UTF8.GetBytes("x");
            form.Add(new StringContent("text/plain"), "contentType");
            form.Add(new StringContent("sha256:x"), "contentHash");
            form.Add(new StringContent("1"), "size");
            var stream = new ByteArrayContent(bytes);
            stream.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(stream, "content", "x.bin");

            using var response = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_MalformedDirectoryEnvelopeReturnsBadRequest()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            // A directory upload whose envelope is not valid JSON must
            // surface as a diagnosable 400 (with an error message) and
            // never an opaque 500.
            var envelope = Encoding.UTF8.GetBytes("not-valid-json");
            using var form = BuildMultipart(
                "specs", envelope,
                "application/x-mohist-artifact-directory",
                "sha256:bad", envelope.LongLength);

            using var response = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task AgentJobUploadEndpoint_AcceptsMultipartForRunningJob()
    {
        var jobId = $"agent-job-upload-{Guid.NewGuid():N}";
        var workId = $"agent-work-{Guid.NewGuid():N}";
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.AssignRunnerAsync("runner-agent-upload", workId);

        var path = "review.md";
        var payload = Encoding.UTF8.GetBytes("agent artifact content");
        using var form = BuildMultipart(path, payload, "text/markdown", "sha256:agent", payload.LongLength);

        using var response = await _fixture.Client.PostAsync(
            $"/api/agent-jobs/{jobId}/work/{workId}/artifact-uploads",
            form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var uploadId = data.GetProperty("uploadId").GetString()!;
        Assert.StartsWith("artup_", uploadId);
        Assert.Equal(jobId, data.GetProperty("workflowRunId").GetString());
        Assert.Equal(workId, data.GetProperty("workId").GetString());
        Assert.Equal(workId, data.GetProperty("taskRunId").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var pending = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UploadId == uploadId);
        Assert.NotNull(pending);
        Assert.Equal(jobId, pending!.WorkflowRunId);
        Assert.Equal(workId, pending.WorkId);
    }

    /// <summary>
    /// Set up a workflow that has been started, assigned by a runner, and
    /// has had its first task polled — that is the only state in which
    /// <c>WorkflowGrain.GetActiveWorkAsync</c> returns a non-null view.
    /// Returns <c>(workflowRunId, workId, runnerId)</c> for the active task.
    /// The caller is responsible for unregistering the runner after
    /// assertions to prevent the heartbeat from failing the task early.
    /// </summary>
    private async Task<(string workflowRunId, string workId, string runnerId)> SetupActiveWorkAsync()
    {
        var projectName = UniqueProjectName("art");
        var projectResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = projectName,
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
            });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var issueResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "needs upload", isDraft = false });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issueJson.GetProperty("data").GetProperty("number").GetInt32();
        var issueId = issueJson.GetProperty("data").GetProperty("id").GetString()!;

        using (var startResp = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        }

        var runnerId = $"upload-test-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        var issueStatus = await issueGrain.GetWorkflowStatusAsync();
        var workflowRunId = issueStatus!.WorkflowRunId!;
        Assert.False(string.IsNullOrEmpty(workflowRunId));

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.AssignWorkerAsync(runnerId);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await TestWait.ForAsync(
            () => runner.PollAsync(_fixture.Services),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive active work");
// Note: the runner is intentionally left registered here. Unregistering
        // would fail the in-flight task via the runner-lost notification, which
        // would break the subsequent upload assertions that require an active
        // task. The runner is short-lived and torn down with the silo.
        return (workflowRunId, work!.WorkId, runnerId);
    }
}
