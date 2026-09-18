using Mohist.Server.Project.Services;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.Tests.Platform;

[Trait("level", "L0")]
public sealed class DoctorCheckServiceTests
{
    private static readonly string[] CanonicalOrder =
    [
        "revision-alignment",
        "migrations",
        "model-catalog",
        "verification-command",
        "project-verification-optional",
    ];

    [Fact]
    public void Evaluate_AllFactsHealthy_ReturnsCanonicalOkChecks()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(projects:
        [
            new DoctorProjectFact("alpha", HasVerificationCommand: true, HasActiveExecution: true, IsOptional: false),
        ]));

        Assert.Equal(CanonicalOrder, checks.Select(check => check.Name));
        Assert.All(checks, check =>
        {
            Assert.Equal("ok", check.Status);
            Assert.Null(check.NextAction);
        });
    }

    [Fact]
    public void Evaluate_RequiredProjectMissingCommand_FailsVerificationCheck()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(projects:
        [
            new DoctorProjectFact("alpha", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false),
        ]));

        var verification = Check(checks, "verification-command");
        Assert.Equal("fail", verification.Status);
        Assert.Contains("alpha", verification.Detail);
        Assert.False(string.IsNullOrWhiteSpace(verification.NextAction));
        Assert.Contains("mo project workflow verification set", verification.NextAction!, StringComparison.Ordinal);
        Assert.Equal("ok", Check(checks, "project-verification-optional").Status);
    }

    [Fact]
    public void Evaluate_InactiveProjectMissingCommand_WarnsOptionalCheck()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(projects:
        [
            new DoctorProjectFact("fixture-a", HasVerificationCommand: false, HasActiveExecution: false, IsOptional: false),
        ]));

        var optional = Check(checks, "project-verification-optional");
        Assert.Equal("warn", optional.Status);
        Assert.Contains("fixture-a", optional.Detail);
        Assert.False(string.IsNullOrWhiteSpace(optional.NextAction));
        Assert.Equal("ok", Check(checks, "verification-command").Status);
    }

    [Fact]
    public void Evaluate_ExplicitlyOptionalActiveProject_WarnsInsteadOfFailing()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(projects:
        [
            new DoctorProjectFact("fixture-b", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: true),
        ]));

        Assert.Equal("ok", Check(checks, "verification-command").Status);
        Assert.Equal("warn", Check(checks, "project-verification-optional").Status);
    }

    [Fact]
    public void Evaluate_StrictPromotesEveryMissingProjectToVerificationFailure()
    {
        var checks = DoctorCheckService.Evaluate(
            Snapshot(projects:
            [
                new DoctorProjectFact("alpha", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false),
                new DoctorProjectFact("fixture-a", HasVerificationCommand: false, HasActiveExecution: false, IsOptional: false),
                new DoctorProjectFact("fixture-b", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: true),
            ]),
            strict: true);

        var verification = Check(checks, "verification-command");
        Assert.Equal("fail", verification.Status);
        Assert.Contains("alpha", verification.Detail);
        Assert.Contains("fixture-a", verification.Detail);
        Assert.Contains("fixture-b", verification.Detail);
        Assert.Equal("ok", Check(checks, "project-verification-optional").Status);
    }

    [Fact]
    public void Evaluate_ProjectVerificationFactsDoNotAffectPlatformOrRunnerChecks()
    {
        var checks = DoctorCheckService.Evaluate(Snapshot(projects:
        [
            new DoctorProjectFact("alpha", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false),
            new DoctorProjectFact("fixture-a", HasVerificationCommand: false, HasActiveExecution: false, IsOptional: false),
        ]));

        Assert.Equal("ok", Check(checks, "revision-alignment").Status);
        Assert.Equal("ok", Check(checks, "migrations").Status);
        Assert.Equal("ok", Check(checks, "model-catalog").Status);
        Assert.Equal("fail", Check(checks, "verification-command").Status);
        Assert.Equal("warn", Check(checks, "project-verification-optional").Status);
    }

    [Fact]
    public void Evaluate_ShuffledProjectFacts_ProduceStableOrderAndSortedNames()
    {
        DoctorProjectFact[] projects =
        [
            new("zeta", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false),
            new("alpha", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false),
            new("omega", HasVerificationCommand: false, HasActiveExecution: false, IsOptional: false),
            new("beta", HasVerificationCommand: false, HasActiveExecution: false, IsOptional: false),
        ];

        var forward = DoctorCheckService.Evaluate(Snapshot(projects: projects));
        var reversed = DoctorCheckService.Evaluate(Snapshot(projects: projects.Reverse().ToArray()));

        Assert.Equal(CanonicalOrder, forward.Select(check => check.Name));
        Assert.Equal(
            forward.Select(check => (check.Name, check.Status, check.Detail)),
            reversed.Select(check => (check.Name, check.Status, check.Detail)));
        Assert.Equal(
            "Required Projects missing verification commands: alpha, zeta",
            Check(forward, "verification-command").Detail);
        Assert.Equal(
            "Optional Projects missing verification commands: beta, omega",
            Check(forward, "project-verification-optional").Detail);
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
            MigrationsCurrent: false,
            Projects: [new DoctorProjectFact("alpha", HasVerificationCommand: false, HasActiveExecution: true, IsOptional: false)],
            IncompleteRuntimeCatalogs: ["runner:openai"]));

        Assert.Equal(["ok", "fail", "fail", "fail", "ok"], checks.Select(check => check.Status));
        Assert.All(checks.Where(check => check.Status == "fail"), check =>
            Assert.False(string.IsNullOrWhiteSpace(check.NextAction)));
        Assert.Contains("alpha", checks[3].Detail);
        Assert.Contains("runner:openai", checks[2].Detail);
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
        Assert.Equal("ok", checks[4].Status);
    }

    [Fact]
    public void IsExplicitlyOptional_MatchesIdsOrdinalAndNamesCaseInsensitively()
    {
        var project = new ProjectInfo { Id = "proj-1", Name = "Alpha" };

        Assert.True(DoctorFactSource.IsExplicitlyOptional(project, ["proj-1"]));
        Assert.True(DoctorFactSource.IsExplicitlyOptional(project, ["ALPHA"]));
        Assert.True(DoctorFactSource.IsExplicitlyOptional(project, [" proj-1 "]));
        Assert.False(DoctorFactSource.IsExplicitlyOptional(project, ["PROJ-1"]));
        Assert.False(DoctorFactSource.IsExplicitlyOptional(project, ["beta"]));
        Assert.False(DoctorFactSource.IsExplicitlyOptional(project, []));
    }

    [Fact]
    public async Task GetChecksAsync_ProjectSourceFailure_FailsOnlyProjectChecks()
    {
        var checks = await new DoctorCheckService(new ThrowingProjectSource()).GetChecksAsync();

        Assert.Equal(["ok", "ok", "ok", "fail", "fail"], checks.Select(check => check.Status));
        Assert.All(
            checks.Where(check => check.Status == "fail"),
            check => Assert.False(string.IsNullOrWhiteSpace(check.NextAction)));
    }

    private static DoctorFactSnapshot Snapshot(
        IReadOnlyDictionary<string, string?>? revisions = null,
        bool migrationsCurrent = true,
        IReadOnlyList<DoctorProjectFact>? projects = null,
        IReadOnlyList<string>? incompleteCatalogs = null) =>
        new(
            new DoctorRevisionFacts(revisions ?? new Dictionary<string, string?> { ["server"] = "r1" }),
            migrationsCurrent,
            projects ?? [],
            incompleteCatalogs ?? []);

    private static DoctorCheck Check(IReadOnlyList<DoctorCheck> checks, string name) =>
        checks.Single(check => check.Name == name);

    private sealed class ThrowingRevisionSource : IDoctorFactSource
    {
        public Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("revision source unavailable");

        public Task<bool> AreMigrationsCurrentAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<DoctorProjectFact>> GetProjectFactsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorProjectFact>>([]);

        public Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ThrowingProjectSource : IDoctorFactSource
    {
        public Task<DoctorRevisionFacts> GetRevisionFactsAsync(CancellationToken ct) =>
            Task.FromResult(new DoctorRevisionFacts(new Dictionary<string, string?> { ["server"] = "r1" }));

        public Task<bool> AreMigrationsCurrentAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<DoctorProjectFact>> GetProjectFactsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("project source unavailable");

        public Task<IReadOnlyList<string>> GetIncompleteRuntimeCatalogsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
