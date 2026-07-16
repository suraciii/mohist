using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.SpecTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class ConfigRoutesSpecs : IAsyncLifetime
{
    private readonly InMemoryConfigDocumentStore _documents = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:Config:serverPort"] = "3456",
            ["Mohist:Config:serverHost"] = "localhost",
        });
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton<IEnvironmentVariableProvider, MockEnvironmentVariableProvider>();
        builder.Services.AddSingleton<ILogger<ConfigService>, LoggerStub<ConfigService>>();
        builder.Services.AddSingleton<ConfigService>(sp => new ConfigService(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IEnvironmentVariableProvider>(),
            sp.GetRequiredService<ILogger<ConfigService>>(),
            _documents));

        _app = builder.Build();
        _app.MapConfigRoutes();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConfig_ExposesLogLevelAndRuntimeSchedulingKeys()
    {
        var body = await _client.GetFromJsonAsync<ApiEnvelope<JsonElement>>("/api/config/");
        Assert.NotNull(body);
        Assert.True(body!.Success);
        var data = body.Data;

        Assert.True(data.TryGetProperty("logLevel", out _));
        Assert.True(data.TryGetProperty("maxConcurrentAgents", out _));
        Assert.True(data.TryGetProperty("agentTimeout", out _));
        Assert.True(data.TryGetProperty("taskTimeout", out _));
        Assert.True(data.TryGetProperty("stageTimeout", out _));
        Assert.True(data.TryGetProperty("pollInterval", out _));
        Assert.True(data.TryGetProperty("maxGracePeriods", out _));

        Assert.DoesNotContain(data.EnumerateObject(), p => LooksLikeSecret(p.Name));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("DEBUG")]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("ERROR")]
    public async Task PutConfigLogLevel_AcceptsSupportedLevel(string level)
    {
        using var response = await _client.PutAsJsonAsync("/api/config/logLevel", new { value = level });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var read = await _client.GetFromJsonAsync<ApiEnvelope<JsonElement>>("/api/config/");
        Assert.True(read!.Success);
        Assert.Equal(level, read.Data.GetProperty("logLevel").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("debug")]
    [InlineData("VERBOSE")]
    [InlineData("TRACE")]
    [InlineData("FATAL")]
    [InlineData("")]
    public async Task PutConfigLogLevel_RejectsUnsupportedValue_AndLeavesPreviousUnchanged(string level)
    {
        using var baseline = await _client.PutAsJsonAsync("/api/config/logLevel", new { value = "WARN" });
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);

        using var response = await _client.PutAsJsonAsync("/api/config/logLevel", new { value = level });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.NotNull(envelope.Error);
        Assert.Contains("logLevel", envelope.Error, StringComparison.OrdinalIgnoreCase);

        var read = await _client.GetFromJsonAsync<ApiEnvelope<JsonElement>>("/api/config/");
        Assert.Equal("WARN", read!.Data.GetProperty("logLevel").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("maxConcurrentAgents", 7)]
    [InlineData("agentTimeout", 900)]
    [InlineData("taskTimeout", 1200)]
    [InlineData("stageTimeout", 7200)]
    [InlineData("pollInterval", 1500)]
    [InlineData("maxGracePeriods", 5)]
    public async Task PutConfigRuntimeKey_AcceptsSupportedKey(string key, int value)
    {
        using var response = await _client.PutAsJsonAsync($"/api/config/{key}", new { value });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var read = await _client.GetFromJsonAsync<ApiEnvelope<JsonElement>>("/api/config/");
        Assert.Equal(value, read!.Data.GetProperty(key).GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PutConfig_RejectsUnknownKey()
    {
        using var response = await _client.PutAsJsonAsync("/api/config/totallyUnknownKey", new { value = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.NotNull(envelope.Error);
        Assert.Contains("Unknown", envelope.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PutConfigRuntimeKey_RejectsNonNumberValue()
    {
        using var response = await _client.PutAsJsonAsync("/api/config/agentTimeout", new { value = "not-a-number" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PutConfig_MissingValue_ReturnsBadRequest()
    {
        using var response = await _client.PutAsJsonAsync("/api/config/logLevel", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static bool LooksLikeSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Key", StringComparison.OrdinalIgnoreCase) && !key.Equals("serverHost", StringComparison.OrdinalIgnoreCase);

    private sealed record ApiEnvelope<T>(bool Success, T Data, string? Error = null, string? Code = null);

    private sealed class LoggerStub<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
