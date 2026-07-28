using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Locks the read-path guarantee that an archived issue's detail API
/// still exposes the workflow run reference and the full execution
/// history. Archive is a visibility-only operation (T-001), and the
/// reconciler no longer treats a Done/archived issue as an active
/// workflow candidate (T-002), so the read path must not drop the
/// reference, must not surface a false "running" indicator, and must
/// keep serving the same sub-resources (workflow status, artifacts,
/// events, feedback) as for a non-archived Done issue.
///
/// Spec: <c>openspec/changes/issue-264/specs/http-api/spec.md</c>.
/// </summary>
[Collection("IntegrationIssue")]
public class IssueArchivedDetailApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public IssueArchivedDetailApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task GetIssue_ForArchivedDoneIssue_ReturnsPreservedWorkflowRunId_AndArchivedAt()
    {
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        var issue = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await issue.ArchiveAsync();

        var detail = await _client.GetDataAsync<IssueDetailWireDto>(
            $"/api/projects/{projectId}/issues/{issueNumber}");

        // Spec scenario 1: archived detail returns the workflow run
        // reference of the completed run and the archive timestamp.
        Assert.Equal(wrId, detail.WorkflowRunId);
        Assert.False(string.IsNullOrWhiteSpace(detail.ArchivedAt),
            "archivedAt must be set on an archived issue detail response");

        // The spec mandates the response shape be the same as a
        // non-archived issue — every other field the detail already
        // exposed must still be present (no execution-history field
        // disappears after archiving).
        Assert.Equal("done", detail.Status);
        Assert.Equal(issueNumber, detail.Number);
        Assert.NotNull(detail.Title);
        Assert.NotNull(detail.Feedback);
        Assert.NotNull(detail.Prereq);
        Assert.NotNull(detail.Comments);
        Assert.NotNull(detail.Attachments);
    }

    [Fact]
    public async Task GetIssue_ForArchivedDoneIssue_JsonWireShape_KeepsReferenceAndArchivedAt()
    {
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        var issue = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await issue.ArchiveAsync();

        var raw = await _client.GetRawAsync($"/api/projects/{projectId}/issues/{issueNumber}");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        // Spec scenario 1: workflowRunId and archivedAt are present on
        // the wire for an archived Done issue.
        Assert.True(data.TryGetProperty("workflowRunId", out var wrIdEl),
            "archived detail must include workflowRunId");
        Assert.Equal(wrId, wrIdEl.GetString());
        Assert.True(data.TryGetProperty("archivedAt", out var archivedAtEl),
            "archived detail must include archivedAt");
        Assert.False(string.IsNullOrWhiteSpace(archivedAtEl.GetString()));
        // The legacy "activeWorkflowRunId" alias must not appear at
        // all — design D2 collapsed the dual property.
        Assert.False(data.TryGetProperty("activeWorkflowRunId", out _),
            "archived detail must not expose the legacy activeWorkflowRunId alias");
    }

    [Fact]
    public async Task GetIssue_ForArchivedAndNonArchivedDoneIssue_ReturnIdenticalExecutionHistoryFields()
    {
        // Spec scenario 2: timeline/artifacts/events/feedback sub-resources
        // return the same shape for archived and non-archived Done issues.
        var archivedSeed = await SeedDoneIssueWithWorkflowRunAsync();
        var nonArchivedSeed = await SeedDoneIssueWithWorkflowRunAsync();

        // Inject two feedback records on the archived run so the
        // feedback sub-resource is non-empty and shape-comparable.
        var archivedBaseTime = TestTime.UtcNow.AddMinutes(-10);
        var archivedRun = await LoadWorkflowRunAsync(archivedSeed.wrId);
        Assert.NotNull(archivedRun);
        archivedRun!.Feedback.Add(new ApprovalFeedback(
            Id: $"fb_archived_first_{Guid.NewGuid():N}",
            WorkflowRunId: archivedSeed.wrId,
            Stage: "plan",
            Body: "archived feedback body",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: archivedBaseTime));
        await SaveWorkflowRunAsync(archivedSeed.wrId, archivedRun);

        var nonArchivedBaseTime = TestTime.UtcNow.AddMinutes(-10);
        var nonArchivedRun = await LoadWorkflowRunAsync(nonArchivedSeed.wrId);
        Assert.NotNull(nonArchivedRun);
        nonArchivedRun!.Feedback.Add(new ApprovalFeedback(
            Id: $"fb_nonarchived_first_{Guid.NewGuid():N}",
            WorkflowRunId: nonArchivedSeed.wrId,
            Stage: "plan",
            Body: "non-archived feedback body",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: nonArchivedBaseTime));
        await SaveWorkflowRunAsync(nonArchivedSeed.wrId, nonArchivedRun);

        // Archive only the first issue.
        var archivedIssueGrain = _grains.GetGrain<IIssueGrain>(
            IssueGrainKey(archivedSeed.projectId, archivedSeed.issueNumber));
        await archivedIssueGrain.ArchiveAsync();

        var archivedDetail = await _client.GetDataAsync<IssueDetailWireDto>(
            $"/api/projects/{archivedSeed.projectId}/issues/{archivedSeed.issueNumber}");
        var nonArchivedDetail = await _client.GetDataAsync<IssueDetailWireDto>(
            $"/api/projects/{nonArchivedSeed.projectId}/issues/{nonArchivedSeed.issueNumber}");

        // Both detail responses expose the same shape for the
        // execution-history-bearing sub-resources: feedback, comments,
        // attachments, prerequisites.
        Assert.NotNull(archivedDetail.Feedback);
        Assert.NotNull(nonArchivedDetail.Feedback);
        Assert.Single(archivedDetail.Feedback);
        Assert.Single(nonArchivedDetail.Feedback);
        Assert.Equal(archivedSeed.wrId, archivedDetail.Feedback![0].WorkflowRunId);
        Assert.Equal(nonArchivedSeed.wrId, nonArchivedDetail.Feedback![0].WorkflowRunId);
        Assert.Equal("plan", archivedDetail.Feedback[0].Stage);
        Assert.Equal("plan", nonArchivedDetail.Feedback[0].Stage);
        Assert.Equal("archived feedback body", archivedDetail.Feedback[0].Body);
        Assert.Equal("non-archived feedback body", nonArchivedDetail.Feedback[0].Body);
    }

    [Fact]
    public async Task GetIssue_WorkflowArtifacts_ForArchivedDoneIssue_ReturnsRecordedArtifacts()
    {
        // Spec scenario 2: artifacts sub-resource returns the same
        // shape for archived and non-archived Done issues. Pre-archive
        // the issue and assert the artifacts list is identical.
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        await SeedArtifactAsync(wrId, "openspec/changes/issue-1/proposal.md", "proposal body");

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        var raw = await _client.GetRawAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/artifacts");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(1, data.GetArrayLength());

        var entry = data[0];
        Assert.Equal(wrId, entry.GetProperty("workflowRunId").GetString());
        Assert.Equal("openspec/changes/issue-1/proposal.md", entry.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GetIssue_WorkflowEvents_ForArchivedDoneIssue_ReturnsMergedTimeline()
    {
        // Spec scenario 2: events sub-resource merges issue and
        // workflow-run events. Archive must not collapse the timeline.
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        var raw = await _client.GetRawAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/events?limit=200");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);

        // The workflow timeline (source = /mohist/workflow-runs/{wrId})
        // must still be merged in for an archived issue; without the
        // preserved reference this would be just issue-events.
        var hasWorkflowEvent = false;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.TryGetProperty("source", out var sourceEl) &&
                sourceEl.GetString() is { } source &&
                source.Contains($"/mohist/workflow-runs/{wrId}", StringComparison.Ordinal))
            {
                hasWorkflowEvent = true;
                break;
            }
        }
        Assert.True(hasWorkflowEvent,
            "archived detail events must still include workflow-run events for the preserved reference");
    }

    [Fact]
    public async Task GetIssue_WorkflowStatus_ForArchivedDoneIssue_ReturnsCompletedRunSnapshot()
    {
        // Spec scenario 2: workflow timeline sub-resource returns the
        // same shape for archived and non-archived Done issues. The
        // status endpoint reads via the grain; the snapshot it returns
        // for a completed run must be surfaced on an archived issue
        // too because the reference is preserved.
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        await MarkWorkflowRunCompletedAsync(wrId);

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        var status = await _client.GetDataAsync<WorkflowStatusEnvelopeDto>(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");

        Assert.NotNull(status.Workflow);
        // Status is serialized as the enum name (lowercase via the
        // JsonStringEnumConverter).
        Assert.Equal("completed", status.Workflow!.Status);
    }

    [Fact]
    public async Task GetIssue_ForArchivedDoneIssue_HealthAndStatus_IndicateDoneNotActive()
    {
        // Spec scenario 3: the archived detail response must not
        // indicate an active/running workflow solely because
        // workflowRunId is present. The health/status fields project
        // the issue lifecycle, not reference presence.
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        await MarkWorkflowRunCompletedAsync(wrId);

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        var raw = await _client.GetRawAsync($"/api/projects/{projectId}/issues/{issueNumber}");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        // The preserved reference is present (otherwise we cannot
        // render the timeline at all), but the health/status/attention
        // fields must not signal "active" or "running".
        Assert.Equal(wrId, data.GetProperty("workflowRunId").GetString());
        Assert.Equal("done", data.GetProperty("status").GetString());
        Assert.Equal("done", data.GetProperty("health").GetString());

        // workflowStatus reflects the workflow-run lifecycle (a
        // completed run is "completed"), not "running".
        Assert.True(data.TryGetProperty("workflowStatus", out var workflowStatus));
        Assert.Equal("completed", workflowStatus.GetString());

        // No "attention" indicator is raised for a Done/archived issue
        // with a preserved reference — attention is only projected for
        // running workflows awaiting approval or failed runs.
        Assert.False(data.TryGetProperty("attention", out var attention) && attention.ValueKind != JsonValueKind.Null,
            "archived Done issue must not surface an 'attention' indicator");
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("rerun-from-stage")]
    [InlineData("force-stop")]
    [InlineData("stop")]
    public async Task WorkflowControl_ForArchivedDoneIssue_IsRejectedAsNotActive(string action)
    {
        var (projectId, issueNumber, _, wrId) = await SeedDoneIssueWithWorkflowRunAsync();
        await MarkWorkflowRunCompletedAsync(wrId);

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        using var response = action switch
        {
            "approve" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { author = "supervisor" }),
            "reject" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { author = "supervisor", message = "historical" }),
            "rerun-from-stage" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { stage = "build" }),
            _ => await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", null),
        };

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("not active", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("rerun-from-stage")]
    [InlineData("force-stop")]
    [InlineData("stop")]
    public async Task WorkflowControl_ForInProgressIssueWithCompletedWorkflow_IsRejectedAsNotActive(string action)
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await MarkWorkflowRunCompletedAsync(wrId);

        using var response = action switch
        {
            "approve" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { author = "supervisor" }),
            "reject" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { author = "supervisor", message = "historical" }),
            "rerun-from-stage" => await _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", new { stage = "build" }),
            _ => await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/{action}", null),
        };

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("not active", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetIssue_ForArchivedDoneIssue_JsonWireShape_DoesNotExposeLegacyActiveAlias()
    {
        // T-001/D2 collapsed the dual property to a single
        // WorkflowRunId. The wire shape on the archived detail must
        // match that — no "activeWorkflowRunId" key, even on a Done
        // issue that has a preserved reference.
        var (projectId, issueNumber, _, _) = await SeedDoneIssueWithWorkflowRunAsync();

        var grain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, issueNumber));
        await grain.ArchiveAsync();

        var raw = await _client.GetRawAsync($"/api/projects/{projectId}/issues/{issueNumber}");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        Assert.False(data.TryGetProperty("activeWorkflowRunId", out _),
            "archived detail must not expose the legacy 'activeWorkflowRunId' alias; only 'workflowRunId' is canonical");
    }

    [Fact]
    public async Task GetIssue_ForArchivedAndNonArchivedDoneIssue_WorkflowRunIdField_IsIdenticallyNamed()
    {
        // Spec scenario 2 + scenario 3 together: the response shape
        // for archived and non-archived issues is the same JSON
        // contract, including the field name "workflowRunId".
        var (projectIdArchived, issueNumberArchived, _, wrIdArchived) =
            await SeedDoneIssueWithWorkflowRunAsync();
        var (projectIdDone, issueNumberDone, _, wrIdDone) =
            await SeedDoneIssueWithWorkflowRunAsync();

        var archivedGrain = _grains.GetGrain<IIssueGrain>(
            IssueGrainKey(projectIdArchived, issueNumberArchived));
        await archivedGrain.ArchiveAsync();

        var archivedRaw = await _client.GetRawAsync(
            $"/api/projects/{projectIdArchived}/issues/{issueNumberArchived}");
        var doneRaw = await _client.GetRawAsync(
            $"/api/projects/{projectIdDone}/issues/{issueNumberDone}");

        using var archivedDoc = JsonDocument.Parse(archivedRaw);
        using var doneDoc = JsonDocument.Parse(doneRaw);

        var archivedData = archivedDoc.RootElement.GetProperty("data");
        var doneData = doneDoc.RootElement.GetProperty("data");

        // Both responses have workflowRunId at the same JSON path
        // pointing to the preserved reference.
        Assert.Equal(wrIdArchived, archivedData.GetProperty("workflowRunId").GetString());
        Assert.Equal(wrIdDone, doneData.GetProperty("workflowRunId").GetString());

        // Archived-only field is "archivedAt" — same field name, just
        // populated with a timestamp instead of absent.
        Assert.True(archivedData.TryGetProperty("archivedAt", out var archivedAtEl) &&
                    !string.IsNullOrWhiteSpace(archivedAtEl.GetString()));
    }

    private async Task<(string projectId, int issueNumber, string issueId, string wrId)>
        SeedDoneIssueWithWorkflowRunAsync()
    {
        var (projectId, issueNumber, issueId, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CompleteWorkAsync(wrId);
        return (projectId, issueNumber, issueId, wrId);
    }

    private async Task<(string projectId, int issueNumber, string issueId, string wrId)>
        SeedInProgressIssueWithWorkflowRunAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueId, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();
        return (projectId, issueNumber, issueId, wrId);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"mohist-local-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return (id, name);
    }

    private async Task<(string issueId, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = IssueGrainKey(projectId, number);
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, "Archived detail API", null, null, null, null, isDraft: false);
        return (issueId, number);
    }

    private static string IssueGrainKey(string projectId, int number) =>
        GrainKey.Issue(new IssueKey(projectId, number));

    private Task DispatchEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task MarkWorkflowRunCompletedAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId);
        Assert.NotNull(row);
        var run = JsonSerializer.Deserialize<WorkflowRun>(row!.State, ReadJsonOptions);
        Assert.NotNull(run);
        run!.Status = WorkflowRunStatus.Completed;
        run.CompletedAt = TestTime.UtcNow;
        row.State = JsonSerializer.Serialize(run, ReadJsonOptions);
        await db.SaveChangesAsync();
        // Deactivate the workflow grain so it re-reads the updated
        // state on the next request — otherwise the status endpoint
        // would serve the in-memory snapshot from StartWorkAsync.
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).Deactivate();
    }

    private async Task SeedArtifactAsync(string wrId, string path, string body)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        db.WorkflowArtifacts.Add(new WorkflowArtifactRow
        {
            ArtifactId = $"artifact_{Guid.NewGuid():N}",
            WorkflowRunId = wrId,
            TaskRunId = string.Empty,
            Path = path,
            Kind = "file",
            ContentType = "text/markdown",
            Size = body.Length,
            RecordedAt = TestTime.UtcNow,
            DisplayName = path,
            ArtifactStoragePath = $"memory://{wrId}/{path}",
        });
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun?> LoadWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? null : JsonSerializer.Deserialize<WorkflowRun>(row.State, ReadJsonOptions);
    }

    private async Task SaveWorkflowRunAsync(string workflowRunId, WorkflowRun run)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId);
        Assert.NotNull(row);
        row!.State = JsonSerializer.Serialize(run, ReadJsonOptions);
        await db.SaveChangesAsync();
    }

    private sealed record IssueDetailWireDto(
        int Number,
        string Id,
        string Title,
        string Status,
        string Health,
        string? WorkflowRunId,
        string? WorkflowStatus,
        string? ArchivedAt,
        FeedbackWireDto[] Feedback,
        PrereqWireDto[] Prereq,
        CommentWireDto[] Comments,
        AttachmentWireDto[] Attachments);

    private sealed record FeedbackWireDto(
        string Id,
        int IssueNumber,
        string WorkflowRunId,
        string Stage,
        string Status,
        string Body,
        string CreatedAt);

    private sealed record PrereqWireDto(int Number, bool Completed);

    private sealed record CommentWireDto(string Id, string Body);

    private sealed record AttachmentWireDto(string Id, string FileName);

    private sealed record WorkflowStatusEnvelopeDto(WorkflowStatusBodyDto? Workflow);

    private sealed record WorkflowStatusBodyDto(string Status, string? CurrentStage, WorkflowStageWireDto[] Stages);

    private sealed record WorkflowStageWireDto(string Stage, string Status);
}
