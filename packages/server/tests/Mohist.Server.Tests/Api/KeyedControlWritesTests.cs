using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Idempotency;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L0")]
public sealed class KeyedControlWritesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClassifiedOutcomeIsRecordedWhenTheClaimingAttemptAbandonedTheRow()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var fence = new IdempotencyFence(new TestDbContextFactory(database.Options), new FakeTimeProvider(Now));
        const string command = IdempotencyCommands.WorkflowControl;
        const string scope = "wr_1|caller-a|key-a";
        const string fingerprint = "fingerprint-a";
        var executions = 0;

        var first = await ExecutePauseAsync(
            fence,
            command,
            scope,
            fingerprint,
            "mo run pause wr_1 --idempotency-key",
            "key-a",
            async () =>
            {
                executions++;
                // The attempt that claimed the row fails unclassified and
                // removes it while this request is still deciding.
                await fence.AbandonPendingAsync(command, scope, owned: true);
                return KeyedControlWrites.Outcome.Accepted(ApiResults.SuccessEnvelope());
            });

        Assert.Equal(1, executions);
        Assert.Equal(StatusCodes.Status200OK, await StatusOfAsync(first));
        var recorded = await fence.FindAsync(command, scope);
        Assert.NotNull(recorded);
        Assert.Equal(IdempotencyMappingStates.Completed, recorded!.State);

        // The key now carries the decision this caller already received, so a
        // retry replays it instead of executing again.
        var replay = await ExecutePauseAsync(
            fence,
            command,
            scope,
            fingerprint,
            "mo run pause wr_1 --idempotency-key",
            "key-a",
            () =>
            {
                executions++;
                return Task.FromResult(KeyedControlWrites.Outcome.Accepted(ApiResults.SuccessEnvelope()));
            });

        Assert.Equal(1, executions);
        Assert.Equal(StatusCodes.Status200OK, await StatusOfAsync(replay));
    }

    [Fact]
    public async Task PendingKeyReportsTheSameCommandWithTheQuotedCallerKey()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(Now);
        var fence = new IdempotencyFence(new TestDbContextFactory(database.Options), time);
        const string command = IdempotencyCommands.WorkflowControl;
        const string scope = "wr_1|caller-a|key with space";
        await fence.GetOrCreateAsync(command, scope, "caller-a", "fingerprint-a", null, null);

        var pending = await ExecutePauseAsync(
            fence,
            command,
            scope,
            "fingerprint-a",
            "mo run pause wr_1 --idempotency-key",
            "key with space",
            () => throw new InvalidOperationException("a pending key must not execute"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, await StatusOfAsync(pending));
        var body = await BodyOfAsync(pending);
        Assert.Equal("operation_pending", body.GetProperty("code").GetString());
        Assert.Equal(
            "mo run pause wr_1 --idempotency-key 'key with space'",
            body.GetProperty("nextAction").GetString());
    }

    private static Task<IResult> ExecutePauseAsync(
        IdempotencyFence fence,
        string command,
        string scopeKey,
        string fingerprint,
        string recoveryCommand,
        string key,
        Func<Task<KeyedControlWrites.Outcome>> operation) =>
        KeyedControlWrites.ExecuteAsync(
            new IdempotencyKeyValidation(IdempotencyKeyDisposition.Valid, key),
            new StubCurrentUser("caller-a"),
            fence,
            new FakeTimeProvider(Now),
            command,
            scopeKey,
            fingerprint,
            recoveryCommand,
            operation);

    private static async Task<int> StatusOfAsync(IResult result)
    {
        var context = Context();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static async Task<JsonElement> BodyOfAsync(IResult result)
    {
        var context = Context();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static readonly ServiceProvider RequestServices = new ServiceCollection()
        .AddOptions()
        .AddLogging()
        .BuildServiceProvider();

    private static DefaultHttpContext Context() =>
        new() { RequestServices = RequestServices, Response = { Body = new MemoryStream() } };

    private sealed class StubCurrentUser(string principalId) : ICurrentUser
    {
        public MohistPrincipal Principal { get; } =
            new(principalId, PrincipalKind.Service, principalId, []);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
