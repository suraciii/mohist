using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.L0Tests.SystemSpecs;

[Trait("level", "L0")]
public sealed class DoctorCheckServiceTests
{
    [Fact]
    public void Evaluate_AllFactsHealthy_ReturnsCanonicalOkChecks()
    {
        var checks = DoctorCheckService.Evaluate(new DoctorFactSnapshot(
            new DoctorRevisionFacts(new Dictionary<string, string?>
            {
                ["server"] = "r1",
                ["runner:one"] = "r1",
                ["cli"] = "r1",
                ["slack"] = "r1",
            }),
            true,
            [],
            []));

        Assert.Equal(
            ["revision-alignment", "migrations", "verification-command", "model-catalog"],
            checks.Select(check => check.Name));
        Assert.All(checks, check =>
        {
            Assert.Equal("ok", check.Status);
            Assert.Null(check.NextAction);
        });
    }

    [Fact]
    public void Evaluate_RevisionMismatch_FailsOnlyRevisionCheck()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(revisions: new Dictionary<string, string?>
        {
            ["server"] = "r1",
            ["runner"] = "r2",
        }));

        Assert.Equal("fail", checks[0].Status);
        Assert.Contains("Deploy", checks[0].NextAction);
        Assert.Equal("ok", checks[1].Status);
    }

    [Fact]
    public void Evaluate_MultipleDeploymentFailures_AreIndependentAndActionable()
    {
        var checks = DoctorCheckService.Evaluate(new DoctorFactSnapshot(
            new DoctorRevisionFacts(new Dictionary<string, string?> { ["server"] = "r1" }),
            false,
            ["alpha"],
            ["runner:openai"]));

        Assert.Equal(["ok", "fail", "fail", "fail"], checks.Select(check => check.Status));
        Assert.All(checks.Where(check => check.Status == "fail"), check =>
            Assert.False(string.IsNullOrWhiteSpace(check.NextAction)));
        Assert.Contains("alpha", checks[2].Detail);
        Assert.Contains("runner:openai", checks[3].Detail);
    }

    [Fact]
    public async Task GetChecksAsync_SourceFailure_DoesNotSuppressOtherChecks()
    {
        var source = new ThrowingRevisionSource();
        var checks = await new DoctorCheckService(source).GetChecksAsync();

        Assert.Equal("fail", checks[0].Status);
        Assert.Equal("ok", checks[1].Status);
        Assert.Equal("ok", checks[2].Status);
        Assert.Equal("ok", checks[3].Status);
    }

    private static DoctorFactSnapshot Snapshot(
        IReadOnlyDictionary<string, string?>? revisions = null) =>
        new(
            new DoctorRevisionFacts(revisions ?? new Dictionary<string, string?> { ["server"] = "r1" }),
            true,
            [],
            []);

    private sealed class ThrowingRevisionSource : IDoctorFactSource
    {
        public Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("revision source unavailable");

        public Task<bool> AreMigrationsCurrentAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetProjectsMissingVerificationCommandsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
