using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Platform.Api;

[Trait("level", "L1")]
public sealed class DoctorApiSpecs(DefaultMohistIntegrationFixture fixture) : IClassFixture<DefaultMohistIntegrationFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetChecks_RequiresOperatorScope()
    {
        using var response = await fixture.Client.GetAsync("/api/doctor/checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var checks = payload.GetProperty("data");
        Assert.Equal(4, checks.GetArrayLength());
        foreach (var check in checks.EnumerateArray())
            Assert.Equal(["name", "status", "detail", "nextAction"], check.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task GetChecks_RejectsNonOperatorScope()
    {
        var token = await CreatePatAsync("doctor-readonly", "readonly");
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/doctor/checks");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<string> CreatePatAsync(string name, string scope)
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/auth/tokens",
            new { name, scope, ttlHours = 720 });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return payload.GetProperty("data").GetProperty("token").GetString()!;
    }
}
