using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.Telemetry)]
public class OtelServiceRegistrationSpecs : IAsyncLifetime
{
    private MohistDbFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new MohistDbFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [Fact]
    public void OtelDb_IsResolvableThroughServiceGraph()
    {
        var db = _fixture.Services.GetService<OtelDb>();
        Assert.NotNull(db);
    }

    [Fact]
    public void OtelCollectorStatus_IsResolvableThroughServiceGraph()
    {
        var status = _fixture.Services.GetService<OtelCollectorStatus>();
        Assert.NotNull(status);
    }

    [Fact]
    public void OtelDb_OpensReadOnlyConnection()
    {
        var db = _fixture.Services.GetRequiredService<OtelDb>();
        // Open the read-write connection first so the file is created
        // and the schema is initialized; a read-only connection
        // cannot create a missing file.
        using (var readWrite = db.OpenReadWriteConnection())
        {
            Assert.Equal(System.Data.ConnectionState.Open, readWrite.State);
        }

        using var connection = db.OpenReadOnlyConnection();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}
