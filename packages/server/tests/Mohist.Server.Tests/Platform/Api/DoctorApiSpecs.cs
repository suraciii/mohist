using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Platform.Api;

[Trait("level", "L1")]
public sealed class DoctorApiSpecs(IsolatedMohistIntegrationFixture fixture)
    : IClassFixture<IsolatedMohistIntegrationFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CanonicalCheckOrder =
    [
        "revision-alignment",
        "migrations",
        "model-catalog",
        "verification-command",
        "project-verification-optional",
    ];

    private static readonly string[] ContractKeys = ["name", "status", "detail", "nextAction"];

    private static readonly string[] AllowedStatuses = ["ok", "warn", "fail"];

    [Fact]
    public async Task GetChecks_ReturnsFiveCanonicalChecksWithContractKeysAndStatuses()
    {
        using var response = await fixture.Client.GetAsync("/api/doctor/checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = await ReadChecksAsync(response);
        Assert.Equal(CanonicalCheckOrder, checks.Select(check => check.Name));
        foreach (var check in checks)
        {
            Assert.Equal(ContractKeys, check.Keys);
            Assert.Contains(check.Status, AllowedStatuses);
        }
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

    [Fact]
    public async Task GetChecks_InactiveProjectWithoutCommand_WarnsOptionalAndKeepsPlatformChecksOk()
    {
        await SeedInactiveProjectWithoutVerificationCommandAsync("doctor-fixture-inactive");

        using var response = await fixture.Client.GetAsync("/api/doctor/checks");
        var checks = await ReadChecksAsync(response);

        Assert.Equal("ok", Check(checks, "verification-command").Status);
        var optional = Check(checks, "project-verification-optional");
        Assert.Equal("warn", optional.Status);
        Assert.Contains("doctor-fixture-inactive", optional.Detail);
        Assert.Equal("ok", Check(checks, "revision-alignment").Status);
        Assert.Equal("ok", Check(checks, "migrations").Status);
        Assert.Equal("ok", Check(checks, "model-catalog").Status);
    }

    [Fact]
    public async Task GetChecks_StrictPromotesInactiveProjectToVerificationFailure()
    {
        await SeedInactiveProjectWithoutVerificationCommandAsync("doctor-fixture-strict");

        using var response = await fixture.Client.GetAsync("/api/doctor/checks?strict=true");
        var checks = await ReadChecksAsync(response);

        var verification = Check(checks, "verification-command");
        Assert.Equal("fail", verification.Status);
        Assert.Contains("doctor-fixture-strict", verification.Detail);
        Assert.Equal("ok", Check(checks, "project-verification-optional").Status);
    }

    private static DoctorCheckSnapshot Check(IReadOnlyList<DoctorCheckSnapshot> checks, string name) =>
        checks.Single(check => check.Name == name);

    private static async Task<List<DoctorCheckSnapshot>> ReadChecksAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return payload.GetProperty("data").EnumerateArray()
            .Select(check => new DoctorCheckSnapshot(
                check.GetProperty("name").GetString()!,
                check.GetProperty("status").GetString()!,
                check.GetProperty("detail").GetString()!,
                [.. check.EnumerateObject().Select(property => property.Name)]))
            .ToList();
    }

    private async Task SeedInactiveProjectWithoutVerificationCommandAsync(string projectId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        if (await db.Projects.AnyAsync(project => project.Id == projectId))
            return;

        var now = fixture.TimeProvider.GetUtcNow();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
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

    private sealed record DoctorCheckSnapshot(string Name, string Status, string Detail, string[] Keys);
}
