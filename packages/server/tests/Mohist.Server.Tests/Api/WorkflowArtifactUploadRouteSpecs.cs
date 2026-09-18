using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
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

    [Fact]
    public async Task RunnerWorkspaceArtifacts_ReadsBoundContentAndRejectsDifferentRunner()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"tasks\":[]}");
            using var form = BuildMultipart(
                "PLANS/tasks.json",
                payload,
                "application/json",
                "sha256:tasks",
                payload.LongLength);
            using var upload = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var uploadData = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var uploadId = uploadData.GetProperty("uploadId").GetString()!;
            var actionAttemptId = uploadData.GetProperty("actionAttemptId").GetString()!;

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId,
                actionAttemptId,
                status = "completed",
                artifacts = new[] { new { path = "PLANS/tasks.json" } },
                artifactUploadIds = new[] { uploadId },
                addTasks = new[]
                {
                    new
                    {
                        id = "next",
                        title = "Next",
                        uses = "spec/task",
                        with = new { },
                    },
                },
            });
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);

            var nextWork = await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Services);
            Assert.NotNull(nextWork);
            var nextWorkId = nextWork.WorkId;
            using var list = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listData = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var artifacts = listData.GetProperty("artifacts");
            Assert.Single(artifacts.EnumerateArray());
            var artifactId = artifacts[0].GetProperty("artifactId").GetString()!;
            Assert.Equal("PLANS/tasks.json", artifacts[0].GetProperty("path").GetString());
            Assert.Equal("sha256:tasks", artifacts[0].GetProperty("contentHash").GetString());

            using var content = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts/{artifactId}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal(payload, await content.Content.ReadAsByteArrayAsync());

            using var otherRunner = await _fixture.Client.GetAsync(
                $"/api/runner/other-runner/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts");
            Assert.Equal(HttpStatusCode.Forbidden, otherRunner.StatusCode);
            var otherBody = await otherRunner.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("workflow_runner_not_assigned", otherBody.GetProperty("code").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task RunnerWorkspaceArtifacts_DoesNotExposePendingUpload()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var payload = Encoding.UTF8.GetBytes("pending");
            using var form = BuildMultipart("PLANS/pending.md", payload, "text/markdown", "sha256:pending", payload.LongLength);
            using var upload = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

            using var list = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{workId}/workspace-artifacts");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var artifacts = (await list.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("data")
                .GetProperty("artifacts");
            Assert.Empty(artifacts.EnumerateArray());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task UploadEndpoint_UnknownWorkItemReturnsNotFound()
    {
        var form = BuildMultipart("review.md", new byte[] { 0x01, 0x02 }, "text/markdown", "sha256:zzz", 2);
        using var response = await _fixture.Client.PostAsync(
            $"/api/workflow-runs/wr_does_not_exist/work/task-1.1/artifact-uploads",
            form);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_NonMultipartReturnsBadRequest()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/api/workflow-runs/wr_anything/work/task-1.1/artifact-uploads",
            content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

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

    [Fact]
    public async Task AgentJobUploadEndpoint_AcceptsMultipartForRunningJob()
    {
        var jobId = $"agent-job-upload-{Guid.NewGuid():N}";
        var runnerId = $"agent-upload-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-upload-project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            new RunnerInfo(
                runnerId,
                ["spec/task"],
                "test-host",
                projectId,
                ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration,
                RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()),
            TestRunnerGenerationExtensions.ProcessGeneration);
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "upload agent artifact",
            ProjectId: projectId,
            AgentId: "agent-test"));
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var dispatch = await runner.PollAsync(_fixture.Services);
        Assert.NotNull(dispatch);
        var workId = dispatch.WorkId;

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
        Assert.Equal(workId, data.GetProperty("actionAttemptId").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var pending = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UploadId == uploadId);
        Assert.NotNull(pending);
        Assert.Equal(jobId, pending!.WorkflowRunId);
        Assert.Equal(workId, pending.WorkId);
    }

    [Fact]
    public async Task DirectoryUpload_EndToEnd_ReturnsCreatedAndReadsEveryEntry()
    {
        var (workflowRunId, workId, runnerId) = await SetupActiveWorkAsync();
        try
        {
            var fileA = Encoding.UTF8.GetBytes("alpha content");
            var fileB = Encoding.UTF8.GetBytes("beta content");
            var files = new[]
            {
                new DirectoryEnvelopeTestFile("a.md", fileA, "text/markdown", Sha256(fileA)),
                new DirectoryEnvelopeTestFile("sub/b.md", fileB, "text/markdown", Sha256(fileB)),
            };
            var envelope = DirectoryEnvelopeTestData.Create(files);

            using var form = BuildMultipart(
                "specs",
                envelope,
                WorkflowArtifactDirectoryEnvelopeReader.ContentType,
                "sha256:dir-envelope",
                envelope.LongLength);

            using var upload = await _fixture.Client.PostAsync(
                $"/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
                form);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var uploadData = (await upload.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("data");
            var uploadId = uploadData.GetProperty("uploadId").GetString()!;
            var actionAttemptId = uploadData.GetProperty("actionAttemptId").GetString()!;
            Assert.Equal("directory", uploadData.GetProperty("kind").GetString());
            Assert.False(uploadData.GetProperty("idempotent").GetBoolean());

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId,
                actionAttemptId,
                status = "completed",
                artifacts = new[] { new { path = "specs" } },
                artifactUploadIds = new[] { uploadId },
                addTasks = new[]
                {
                    new
                    {
                        id = "next",
                        title = "Next",
                        uses = "spec/task",
                        with = new { },
                    },
                },
            });
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);

            var nextWork = await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Services);
            Assert.NotNull(nextWork);
            var nextWorkId = nextWork.WorkId;

            using var list = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var artifacts = (await list.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("data")
                .GetProperty("artifacts");
            Assert.Single(artifacts.EnumerateArray());
            var artifact = artifacts[0];
            Assert.Equal("directory", artifact.GetProperty("kind").GetString());
            var artifactId = artifact.GetProperty("artifactId").GetString()!;

            using var content = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts/{artifactId}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            var entries = (await content.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("data")
                .GetProperty("entries")
                .EnumerateArray()
                .ToDictionary(
                    entry => entry.GetProperty("relativePath").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(files.Length, entries.Count);

            foreach (var file in files)
            {
                Assert.True(entries.TryGetValue(file.Path, out var entry), $"missing entry {file.Path}");
                Assert.Equal(file.Content.LongLength, entry.GetProperty("size").GetInt64());
                Assert.Equal(file.ContentHash, entry.GetProperty("contentHash").GetString());
            }

            using var entryContent = await _fixture.Client.GetAsync(
                $"/api/runner/{runnerId}/workflow-runs/{workflowRunId}/work/{nextWorkId}/workspace-artifacts/{artifactId}/content?file=sub%2Fb.md");
            Assert.Equal(HttpStatusCode.OK, entryContent.StatusCode);
            Assert.Equal(fileB, await entryContent.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private static string Sha256(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";

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
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            UniqueProjectName("art"),
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");

        var issueNumber = await _fixture.Grains
            .GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId))
            .NextAsync();
        await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .CreateAsync(projectId, issueNumber, "needs upload", null, null, null, isDraft: false);

        var workflowRunId = await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .StartWorkAsync();

        var runnerId = $"upload-test-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            new RunnerInfo(
                runnerId,
                ["spec/task", "spec/check"],
                "test-host",
                projectId,
                ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.AssignWorkerAsync(runnerId);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(_fixture.Services);
        Assert.NotNull(work);
        // Note: the runner is intentionally left registered here. Unregistering
        // would fail the in-flight task via the runner-lost notification, which
        // would break the subsequent upload assertions that require an active
        // task. The runner is short-lived and torn down with the silo.
        return (workflowRunId, work.WorkId, runnerId);
    }

    private async Task DispatchEventsAsync()
    {
        await _fixture.Services.GetRequiredService<IEventDispatcher>().DrainAsync();
    }
}
