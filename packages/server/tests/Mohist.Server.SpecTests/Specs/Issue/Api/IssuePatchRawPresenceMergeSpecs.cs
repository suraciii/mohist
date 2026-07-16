using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssuePatchRawPresenceMergeSpecs
{
    private readonly HttpClient _client;

    public IssuePatchRawPresenceMergeSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_AbsentLabels_PreservesExistingLabels()
    {
        var project = await CreateProjectAsync("absent-labels");
        var issue = await CreateIssueAsync(project.Id,
            title: "Has labels",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent("{\"body\":\"new body\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        Assert.Equal("new body", detail.GetProperty("body").GetString());
        var labels = detail.GetProperty("labels").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal("frontend", labels["stream"]);
        Assert.Equal("auth", labels["module"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_NullLabels_ClearsLabelMapToEmpty()
    {
        var project = await CreateProjectAsync("null-labels");
        var issue = await CreateIssueAsync(project.Id,
            title: "Has labels",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent("{\"labels\":null}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("labels").ValueKind);
        Assert.Empty(detail.GetProperty("labels").EnumerateObject());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_PresentLabels_ReplacesLabelMapInFull()
    {
        var project = await CreateProjectAsync("replace-labels");
        var issue = await CreateIssueAsync(project.Id,
            title: "Has labels",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["old"] = "stale",
            });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent(
                "{\"labels\":{\"k\":\"v\"}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        var labels = detail.GetProperty("labels").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Single(labels);
        Assert.Equal("v", labels["k"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_AbsentIsDraft_PreservesExistingDraftState()
    {
        var project = await CreateProjectAsync("absent-isdraft");
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Draft issue", projectId = project.Id, isDraft = true });
        var number = created.GetProperty("number").GetInt32();

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent("{\"title\":\"Updated title\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.Equal("Updated title", detail.Title);
        Assert.True(detail.IsDraft);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_PresentIsDraft_UpdatesDraftState()
    {
        var project = await CreateProjectAsync("present-isdraft");
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Draft issue", projectId = project.Id, isDraft = true });
        var number = created.GetProperty("number").GetInt32();

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent("{\"isDraft\":false}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.False(detail.IsDraft);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_AbsentAttachmentIds_PreservesExistingAttachments()
    {
        var project = await CreateProjectAsync("absent-attachments");
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Plain issue", projectId = project.Id });
        var number = created.GetProperty("number").GetInt32();

        // First PATCH binds an attachment so we have something to preserve.
        var attachment = await UploadAttachmentAsync(project.Id);
        await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                $"{{\"attachmentIds\":[\"{attachment.Id}\"]}}",
                Encoding.UTF8,
                "application/json"));

        // Second PATCH omits attachmentIds entirely; existing bindings survive.
        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent("{\"body\":\"new body\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        var attachmentIds = detail.GetProperty("attachments").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(new[] { attachment.Id }, attachmentIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_PresentAttachmentIds_ReplacesAttachmentList()
    {
        var project = await CreateProjectAsync("replace-attachments");
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Plain issue", projectId = project.Id });
        var number = created.GetProperty("number").GetInt32();

        var first = await UploadAttachmentAsync(project.Id);
        var second = await UploadAttachmentAsync(project.Id);
        await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                $"{{\"attachmentIds\":[\"{first.Id}\",\"{second.Id}\"]}}",
                Encoding.UTF8,
                "application/json"));

        var third = await UploadAttachmentAsync(project.Id);
        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                $"{{\"attachmentIds\":[\"{third.Id}\"]}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        var attachmentIds = detail.GetProperty("attachments").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(new[] { third.Id }, attachmentIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_OnlyLabels_LeavesOtherFieldsUnchanged()
    {
        var project = await CreateProjectAsync("only-labels");
        var attachment = await UploadAttachmentAsync(project.Id);
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Title",
                body = "Original body",
                projectId = project.Id,
                priority = "p1",
                isDraft = true,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                },
            });
        var number = created.GetProperty("number").GetInt32();

        // Bind an attachment via PATCH so attachmentIds has a stored value.
        await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                $"{{\"attachmentIds\":[\"{attachment.Id}\"]}}",
                Encoding.UTF8,
                "application/json"));

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                "{\"labels\":{\"module\":\"auth\"}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        Assert.Equal("Title", detail.GetProperty("title").GetString());
        Assert.Equal("Original body", detail.GetProperty("body").GetString());
        Assert.Equal("p1", detail.GetProperty("priority").GetString());
        Assert.True(detail.GetProperty("isDraft").GetBoolean());
        var attachmentIds = detail.GetProperty("attachments").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(new[] { attachment.Id }, attachmentIds);

        var labels = detail.GetProperty("labels").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Single(labels);
        Assert.Equal("auth", labels["module"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_NullAttachmentIds_ClearsAllAttachments()
    {
        var project = await CreateProjectAsync("null-attachments");
        var created = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Plain issue", projectId = project.Id });
        var number = created.GetProperty("number").GetInt32();

        var attachment = await UploadAttachmentAsync(project.Id);
        await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent(
                $"{{\"attachmentIds\":[\"{attachment.Id}\"]}}",
                Encoding.UTF8,
                "application/json"));

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{number}",
            new StringContent("{\"attachmentIds\":null}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<JsonElement>(response);
        Assert.Empty(detail.GetProperty("attachments").EnumerateArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_StageModels_PersistsViaWorkflowProfilePath()
    {
        var project = await CreateProjectAsync("stage-models");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Stage model issue", projectId = project.Id });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent(
                "{\"model\":\"openai/gpt-5.5\",\"stageModels\":{\"plan\":\"anthropic/claude-sonnet\",\"build\":\"openai/gpt-5.5\"}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.Equal("openai/gpt-5.5", detail.Model);
        Assert.NotNull(detail.StageModels);
        Assert.Equal("anthropic/claude-sonnet", detail.StageModels!["plan"]);
        Assert.Equal("openai/gpt-5.5", detail.StageModels["build"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PatchIssue_StageModelVariants_PersistsViaWorkflowProfilePath()
    {
        var project = await CreateProjectAsync("stage-variants");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Variant issue", projectId = project.Id });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new StringContent(
                "{\"stageModels\":{\"plan\":\"openai/gpt-5.5\"},\"stageModelVariants\":{\"plan\":\"max\"}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        // The variant is stored on the workflow profile's variables bundle
        // (stages.plan.vars.agent.variant); the read path currently surfaces
        // stageModels but not variants. We assert the model merge round-trip
        // to confirm the partial bundle was persisted and didn't drop fields.
        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.NotNull(detail.StageModels);
        Assert.Equal("openai/gpt-5.5", detail.StageModels!["plan"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithStageModels_PersistsViaWorkflowProfilePath()
    {
        var project = await CreateProjectAsync("create-stage-models");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Created with stages",
                projectId = project.Id,
                stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["plan"] = "openai/gpt-5.5",
                },
                model = "openai/gpt-5.5",
            });
        response.EnsureSuccessStatusCode();

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.Equal("openai/gpt-5.5", detail.Model);
        Assert.NotNull(detail.StageModels);
        Assert.Equal("openai/gpt-5.5", detail.StageModels!["plan"]);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new
            {
                name = $"p-{prefix}-{Guid.NewGuid():N}",
                repository = new
                {
                    name = "main",
                    gitUrl = $"file://{Guid.NewGuid():N}",
                    baseBranch = "main",
                },
            });
        return project;
    }

    private async Task<IssueDto> CreateIssueAsync(
        string projectId,
        string title,
        Dictionary<string, string>? labels = null)
    {
        return await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title,
                projectId,
                labels,
            });
    }

    private async Task<AttachmentDto> UploadAttachmentAsync(string projectId)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("PNGDATA"u8.ToArray());
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", $"sample-{Guid.NewGuid():N}.png");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/attachments",
            form);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AttachmentDto(envelope.GetProperty("data").GetProperty("id").GetString()!);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!envelope.GetProperty("success").GetBoolean())
        {
            var error = envelope.TryGetProperty("error", out var err) ? err.GetString() : "<no error>";
            throw new InvalidOperationException($"API request failed: {error}");
        }
        var data = envelope.GetProperty("data");
        return JsonSerializer.Deserialize<T>(data.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(
        int Number,
        string Id,
        string Title,
        string? Body,
        Dictionary<string, string> Labels,
        string Priority,
        bool IsDraft,
        string[] AttachmentIds,
        string? Model,
        Dictionary<string, string>? StageModels);
    private sealed record AttachmentDto(string Id);
}
