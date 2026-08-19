using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Webhooks;

public sealed class WebhookSubscriptionApiSpecs(MohistIntegrationFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task CrudLifecycle_ExcludesArchivedUnlessAllAndNeverReturnsSecret()
    {
        var project = await CreateProjectAsync("webhooks-crud");
        var path = $"/api/projects/{project.Id}/webhook/subscriptions";
        var created = await Client.PostDataAsync<JsonElement>(path, new
        {
            name = "release",
            match = "event.type == \"com.mohist.release\"",
            targetUrl = "https://hooks.example/release",
            secret = "must-not-echo",
        });
        var id = created.GetProperty("id").GetString()!;
        Assert.False(created.TryGetProperty("secret", out _));
        Assert.True(created.GetProperty("hasSecret").GetBoolean());

        await Client.PostOkAsync($"{path}/{id}/disable");
        var disabledVisible = await Client.GetDataAsync<JsonElement>(path);
        Assert.Equal("disabled", disabledVisible.EnumerateArray().Single().GetProperty("status").GetString());
        await Client.PostOkAsync($"{path}/{id}/archive");
        var visible = await Client.GetDataAsync<JsonElement>(path);
        Assert.Empty(visible.EnumerateArray());
        var all = await Client.GetDataAsync<JsonElement>($"{path}?all=true");
        Assert.Equal("archived", all.EnumerateArray().Single().GetProperty("status").GetString());
        Assert.DoesNotContain("must-not-echo", all.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidMatchAndUrl_AreRejected()
    {
        var project = await CreateProjectAsync("webhooks-validation");
        var path = $"/api/projects/{project.Id}/webhook/subscriptions";
        using var invalidMatch = await Client.PostAsJsonAsync(path, new { name = "bad-match", match = "not valid", targetUrl = "https://hooks.example" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMatch.StatusCode);
        using var invalidUrl = await Client.PostAsJsonAsync(path, new { name = "bad-url", match = "event.type == \"com.mohist.release\"", targetUrl = "ftp://hooks.example" });
        Assert.Equal(HttpStatusCode.Conflict, invalidUrl.StatusCode);
    }

    [Fact]
    public async Task ProjectIsolation_HidesSubscriptionAndFailureReadIsScoped()
    {
        var first = await CreateProjectAsync("webhooks-isolation-a");
        var second = await CreateProjectAsync("webhooks-isolation-b");
        var firstPath = $"/api/projects/{first.Id}/webhook/subscriptions";
        var created = await Client.PostDataAsync<JsonElement>(firstPath, new { name = "private", match = "event.type == \"com.mohist.release\"", targetUrl = "https://hooks.example" });
        var id = created.GetProperty("id").GetString()!;
        using var crossProject = await Client.GetAsync($"/api/projects/{second.Id}/webhook/subscriptions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
        var failures = await Client.GetDataAsync<JsonElement>($"/api/projects/{second.Id}/webhook/subscriptions/{id}/failures");
        Assert.Empty(failures.EnumerateArray());
    }

    private Task<ProjectInfo> CreateProjectAsync(string name) =>
        Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>("/api/projects", $"{name}-{Guid.NewGuid():N}");
}
