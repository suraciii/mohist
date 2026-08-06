using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerAppSetupFactsStoreSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(FixedNow);

    [Fact]
    public async Task BeginManagerAppCreate_accepts_matching_fence_and_persists_the_operation()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        var result = await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 0, "operation-1");

        Assert.True(result.Accepted);
        Assert.NotNull(result.Enrollment);
        Assert.Equal(SlackManagerAppLifecycle.Creating, result.Enrollment!.ManagerAppLifecycle);
        Assert.Equal(1, result.Enrollment.ManagerAppOperationFence);
        Assert.Equal("operation-1", result.Enrollment.ManagerAppOperationId);
        Assert.Null(result.Enrollment.ManagerAppOperationOutcome);

        var reloaded = await store.GetAsync("enrollment-1");
        Assert.Equal(SlackManagerAppLifecycle.Creating, reloaded!.ManagerAppLifecycle);
        Assert.Equal(1, reloaded.ManagerAppOperationFence);
        Assert.Equal("operation-1", reloaded.ManagerAppOperationId);
    }

    [Fact]
    public async Task BeginManagerAppCreate_rejects_a_stale_fence_and_returns_the_current_state()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var reader = new SlackWorkspaceEnrollmentStore(factory, _time);
        var writer = new SlackWorkspaceEnrollmentStore(factory, _time);
        var current = await reader.GetAsync("enrollment-1") ?? throw new InvalidOperationException("seed missing");

        var winner = await writer.BeginManagerAppCreateAsync("enrollment-1", current.ManagerAppOperationFence, "operation-a");
        Assert.True(winner.Accepted);

        var stale = await reader.BeginManagerAppCreateAsync("enrollment-1", current.ManagerAppOperationFence, "operation-b");
        Assert.False(stale.Accepted);
        Assert.NotNull(stale.Enrollment);
        Assert.Equal("operation-a", stale.Enrollment!.ManagerAppOperationId);
        Assert.Equal(1, stale.Enrollment.ManagerAppOperationFence);

        var reloaded = await reader.GetAsync("enrollment-1");
        Assert.Equal("operation-a", reloaded!.ManagerAppOperationId);
    }

    [Fact]
    public async Task ApplyManagerAppCreateResult_records_created_and_survives_store_reload()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);
        var begin = await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 0, "operation-1");
        Assert.True(begin.Accepted);

        var result = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 1,
            SlackManagerAppLifecycle.Created,
            redactedOutcome: "created");

        Assert.True(result.Accepted);
        Assert.Equal(SlackManagerAppLifecycle.Created, result.Enrollment!.ManagerAppLifecycle);
        Assert.Equal("created", result.Enrollment.ManagerAppOperationOutcome);

        var reloaded = await store.GetAsync("enrollment-1");
        Assert.Equal(SlackManagerAppLifecycle.Created, reloaded!.ManagerAppLifecycle);
        Assert.Equal("created", reloaded.ManagerAppOperationOutcome);
    }

    [Fact]
    public async Task ApplyManagerAppCreateResult_rejects_a_stale_result_from_a_previous_operation()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        var stale = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 0,
            SlackManagerAppLifecycle.Created,
            redactedOutcome: "created");
        Assert.False(stale.Accepted);
        Assert.Equal(SlackManagerAppLifecycle.NotCreated, stale.Enrollment!.ManagerAppLifecycle);

        var begin = await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 0, "operation-1");
        Assert.True(begin.Accepted);
        var unknown = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 1,
            SlackManagerAppLifecycle.CreateUnknown,
            redactedOutcome: "manifest_update_failed");
        Assert.True(unknown.Accepted);
        var retry = await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 1, "operation-2");
        Assert.True(retry.Accepted);

        var superseded = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 1,
            SlackManagerAppLifecycle.Created,
            redactedOutcome: "created");
        Assert.False(superseded.Accepted);
        Assert.Equal(SlackManagerAppLifecycle.Creating, superseded.Enrollment!.ManagerAppLifecycle);
        Assert.Equal(2, superseded.Enrollment.ManagerAppOperationFence);
    }

    [Fact]
    public async Task Unknown_create_retries_with_a_fresh_operation_and_reaches_created()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);
        await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 0, "operation-1");
        var unknown = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 1,
            SlackManagerAppLifecycle.CreateUnknown,
            redactedOutcome: "app_create_timeout");
        Assert.True(unknown.Accepted);

        var retry = await store.BeginManagerAppCreateAsync("enrollment-1", expectedFence: 1, "operation-2");
        Assert.True(retry.Accepted);
        Assert.Equal(SlackManagerAppLifecycle.Creating, retry.Enrollment!.ManagerAppLifecycle);
        Assert.Equal(2, retry.Enrollment.ManagerAppOperationFence);
        Assert.Equal("operation-2", retry.Enrollment.ManagerAppOperationId);
        Assert.Null(retry.Enrollment.ManagerAppOperationOutcome);

        var done = await store.ApplyManagerAppCreateResultAsync(
            "enrollment-1",
            expectedFence: 2,
            SlackManagerAppLifecycle.Created,
            redactedOutcome: "created");
        Assert.True(done.Accepted);
        Assert.Equal(SlackManagerAppLifecycle.Created, done.Enrollment!.ManagerAppLifecycle);
    }

    [Fact]
    public async Task Socket_validation_reaches_verified_through_candidate_and_awaiting_socket()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        var staged = await store.StageRuntimeCredentialsAsync("enrollment-1");
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged!.RuntimeCredentialValidationState);

        var awaiting = await store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.AwaitingSocket);
        Assert.Equal(SlackRuntimeCredentialValidationState.AwaitingSocket, awaiting!.RuntimeCredentialValidationState);

        var verified = await store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.Verified);
        Assert.Equal(SlackRuntimeCredentialValidationState.Verified, verified!.RuntimeCredentialValidationState);

        var reloaded = await store.GetAsync("enrollment-1");
        Assert.Equal(SlackRuntimeCredentialValidationState.Verified, reloaded!.RuntimeCredentialValidationState);
    }

    [Fact]
    public async Task Failed_validation_restages_as_candidate_and_can_fail_again()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        await store.StageRuntimeCredentialsAsync("enrollment-1");
        var failed = await store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.Failed);
        Assert.Equal(SlackRuntimeCredentialValidationState.Failed, failed!.RuntimeCredentialValidationState);

        var restaged = await store.StageRuntimeCredentialsAsync("enrollment-1");
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, restaged!.RuntimeCredentialValidationState);

        await store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.AwaitingSocket);
        var failedAgain = await store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.Failed);
        Assert.Equal(SlackRuntimeCredentialValidationState.Failed, failedAgain!.RuntimeCredentialValidationState);
    }

    [Fact]
    public async Task ApplySocketValidation_rejects_states_that_skip_a_step()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedEnrollmentAsync(factory);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ApplySocketValidationAsync("enrollment-1", SlackRuntimeCredentialValidationState.Verified));
    }

    [Fact]
    public async Task Begin_and_apply_return_not_found_for_an_unknown_enrollment()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);

        var begin = await store.BeginManagerAppCreateAsync("missing", expectedFence: 0, "operation-1");
        Assert.False(begin.Accepted);
        Assert.Null(begin.Enrollment);

        var apply = await store.ApplyManagerAppCreateResultAsync(
            "missing",
            expectedFence: 0,
            SlackManagerAppLifecycle.Created,
            redactedOutcome: "created");
        Assert.False(apply.Accepted);
        Assert.Null(apply.Enrollment);
    }

    [Fact]
    public async Task Fresh_enrollment_starts_not_created_with_a_zero_fence_and_no_runtime_credentials()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new SlackWorkspaceEnrollmentStore(factory, _time);
        var enrollment = new SlackWorkspaceEnrollment
        {
            Id = "enrollment-fresh",
            WorkspaceTeamId = "T_FRESH",
        };

        await store.CreateAsync(enrollment);

        Assert.Equal(SlackManagerAppLifecycle.NotCreated, enrollment.ManagerAppLifecycle);
        Assert.Equal(0, enrollment.ManagerAppOperationFence);
        Assert.Null(enrollment.ManagerAppOperationId);
        Assert.Null(enrollment.ManagerAppOperationOutcome);
        Assert.Equal(SlackRuntimeCredentialValidationState.NotProvided, enrollment.RuntimeCredentialValidationState);
    }

    private static async Task SeedEnrollmentAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = "enrollment-1",
            WorkspaceTeamId = "T_ENROLLMENT",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            ManagerActorId = "manager-actor-1",
            AuditJson = "[]",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        });
        await db.SaveChangesAsync();
    }
}
