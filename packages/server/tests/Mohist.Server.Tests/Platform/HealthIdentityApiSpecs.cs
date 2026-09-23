using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Api;
using Mohist.Server.SystemInfo;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Platform;

/// <summary>
/// The health probe is the observed-live-runtime surface consumed by the CLI
/// during managed deployment verification. Its canonical identity fields must
/// be readable by name, with <c>gitHash</c> retained only as a legacy alias.
/// </summary>
[Trait("level", "L1")]
public class HealthIdentityApiSpecs : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRuntimeBuildInfo>(new CanonicalRuntimeBuildInfo());

        _app = builder.Build();
        _app.MapHealthRoutes();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_ExposesCanonicalIdentityWithLegacyGitHashAlias()
    {
        var envelope = await _client.GetFromJsonAsync<ApiEnvelope<JsonElement>>("/api/health");
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        var data = envelope.Data;

        Assert.Equal("server", data.GetProperty("component").GetString());
        Assert.Equal("source-sha", data.GetProperty("sourceRevision").GetString());
        Assert.Equal("build-sha", data.GetProperty("buildGitHash").GetString());
        Assert.Equal(1, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("source-sha", data.GetProperty("gitHash").GetString());
        Assert.Equal("tree-sha", data.GetProperty("treeHash").GetString());
        Assert.Equal("digest-sha", data.GetProperty("artifactDigest").GetString());
        Assert.Equal("release-1", data.GetProperty("releaseId").GetString());
        Assert.Equal(4, data.GetProperty("generation").GetInt64());
    }

    private sealed record ApiEnvelope<T>(bool Success, T Data, string? Error = null, string? Code = null);

    private sealed class CanonicalRuntimeBuildInfo : IRuntimeBuildInfo
    {
        public string? Version => "0.0.0+source-sha";
        public string? GitHash => "source-sha";
        public DateTimeOffset StartedAt { get; } = TestTime.UtcNow;
        public string? Component => "server";
        public string? SourceRevision => "source-sha";
        public string? BuildGitHash => "build-sha";
        public string? TreeHash => "tree-sha";
        public string? ArtifactDigest => "digest-sha";
        public string? ReleaseId => "release-1";
        public long Generation => 4;
        public int? SchemaVersion => 1;
    }
}
