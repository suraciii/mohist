using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("IntegrationRunner")]
public class RunnerSlotsApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerSlotsApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<HttpResponseMessage> RegisterRunnerAsync(string runnerId)
    {
        var response = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "slots-api-host",
        });
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<int> ReadPersistedSlotsAsync(string runnerId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId);
        Assert.NotNull(row);
        return row!.Slots;
    }

    [Fact]
    public async Task PatchSlots_PositiveValue_PersistsAndReturnsUpdatedDefinition()
    {
        var runnerId = $"runner-slots-ok-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);

        try
        {
            using var response = await _fixture.Client.PatchAsJsonAsync(
                $"/api/runner/{runnerId}",
                new { slots = 4 });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var data = payload.GetProperty("data");
            Assert.Equal(runnerId, data.GetProperty("runnerId").GetString());
            Assert.Equal(4, data.GetProperty("slots").GetInt32());

            Assert.Equal(4, await ReadPersistedSlotsAsync(runnerId));

            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            Assert.Equal(4, await runner.GetSlotsAsync());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task PatchSlots_Zero_Returns400AndPersistsValueUnchanged()
    {
        var runnerId = $"runner-slots-zero-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);

        try
        {
            var initialSlots = await ReadPersistedSlotsAsync(runnerId);
            Assert.Equal(RunnerDefinitionStore.DefaultSlots, initialSlots);

            using var response = await _fixture.Client.PatchAsJsonAsync(
                $"/api/runner/{runnerId}",
                new { slots = 0 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.Equal("bad_request", payload.GetProperty("code").GetString());

            Assert.Equal(initialSlots, await ReadPersistedSlotsAsync(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
