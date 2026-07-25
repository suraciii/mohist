using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Api;

/// <summary>
/// issue-491 T-002: historical approval rows persisted before
/// <c>decidedBy</c> existed carry no <c>decidedBy</c> field in the stored
/// JSON. Reading them back must surface the field as null, not fail.
/// </summary>
public class ApprovalStatusLegacyJsonTests
{
    [Theory]
    [InlineData("""{"result":"approved","requestedAt":"2026-01-01T00:00:00Z","respondedAt":"2026-01-02T00:00:00Z"}""")]
    [InlineData("""{"result":"rejected","requestedAt":"2026-01-01T00:00:00Z","respondedAt":"2026-01-02T00:00:00Z"}""")]
    public void ApprovalStatus_Deserializes_LegacyPayload_WithoutDecidedBy(string legacyJson)
    {
        var status = JsonSerializer.Deserialize<ApprovalStatus>(legacyJson, JSON.Options);

        Assert.NotNull(status);
        Assert.Null(status!.DecidedBy);
    }

    [Fact]
    public void ApprovalStatus_RoundTrips_DecidedByWhenSet()
    {
        var status = new ApprovalStatus(
            "approved",
            "2026-01-01T00:00:00Z",
            "2026-01-02T00:00:00Z",
            "supervisor");

        var json = JsonSerializer.Serialize(status, JSON.Options);
        var restored = JsonSerializer.Deserialize<ApprovalStatus>(json, JSON.Options);

        Assert.NotNull(restored);
        Assert.Equal("supervisor", restored!.DecidedBy);
    }
}
