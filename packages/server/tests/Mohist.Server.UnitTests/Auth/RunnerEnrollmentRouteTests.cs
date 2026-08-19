using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.TestSupport;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class RunnerEnrollmentRouteTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<EnrollmentTokenState> InvalidEnrollmentTokenStates =>
    [
        EnrollmentTokenState.Unknown,
        EnrollmentTokenState.Expired,
        EnrollmentTokenState.Consumed,
    ];

    public static TheoryData<RunnerEnrollmentRegisterRequest> InvalidRequests =>
    [
        new RunnerEnrollmentRegisterRequest(null, "runner-a", null),
        new RunnerEnrollmentRegisterRequest("token", null, null),
        new RunnerEnrollmentRegisterRequest("token", new string('r', 257), null),
    ];

    [Fact]
    public async Task Register_ValidTokenIssuesCredentialAndConsumesTokenExactlyOnce()
    {
        using var setup = CreateSetup();
        var created = await setup.Store.CreateEnrollmentTokenAsync(Now.AddMinutes(15));

        var first = await RunnerEnrollmentRoutes.RegisterAsync(
            new RunnerEnrollmentRegisterRequest(created.Token, "runner-single-use", null),
            setup.Store,
            setup.Audit,
            setup.Time,
            CancellationToken.None);
        var (firstStatus, firstBody) = await ExecuteAsync(first);

        Assert.Equal(StatusCodes.Status201Created, firstStatus);
        Assert.Equal("runner-single-use", firstBody.GetProperty("data").GetProperty("runnerId").GetString());
        Assert.NotEmpty(firstBody.GetProperty("data").GetProperty("token").GetString()!);
        Assert.Equal(2, setup.Audit.Events.Count);

        var second = await RunnerEnrollmentRoutes.RegisterAsync(
            new RunnerEnrollmentRegisterRequest(created.Token, "runner-single-use", null),
            setup.Store,
            setup.Audit,
            setup.Time,
            CancellationToken.None);
        var (secondStatus, secondBody) = await ExecuteAsync(second);

        Assert.Equal(StatusCodes.Status401Unauthorized, secondStatus);
        Assert.Equal("enrollment_token_invalid", secondBody.GetProperty("code").GetString());
        Assert.Equal(2, setup.Audit.Events.Count);
    }

    [Theory]
    [MemberData(nameof(InvalidEnrollmentTokenStates))]
    public async Task Register_InvalidEnrollmentTokenStates_ReturnTheSameUnauthorizedContract(
        EnrollmentTokenState state)
    {
        using var setup = CreateSetup();
        var token = await setup.PrepareTokenAsync(state);

        var result = await RunnerEnrollmentRoutes.RegisterAsync(
            new RunnerEnrollmentRegisterRequest(token, $"runner-{state}", null),
            setup.Store,
            setup.Audit,
            setup.Time,
            CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("enrollment_token_invalid", body.GetProperty("code").GetString());
        Assert.Empty(setup.Audit.Events);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Register_InvalidRequests_ReturnBadRequestWithoutConsumingToken(
        RunnerEnrollmentRegisterRequest request)
    {
        using var setup = CreateSetup();

        var result = await RunnerEnrollmentRoutes.RegisterAsync(
            request,
            setup.Store,
            setup.Audit,
            setup.Time,
            CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Empty(setup.Audit.Events);
    }

    [Fact]
    public async Task Register_NullBody_ReturnsBadRequest()
    {
        using var setup = CreateSetup();

        var result = await RunnerEnrollmentRoutes.RegisterAsync(
            null!, setup.Store, setup.Audit, setup.Time, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("register_body_required", body.GetProperty("code").GetString());
        Assert.Empty(setup.Audit.Events);
    }

    private static RouteSetup CreateSetup()
    {
        var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(Now);
        return new RouteSetup(
            database,
            new CredentialStore(new TestDbContextFactory(database.Options), time),
            new RecordingAuthAuditRecorder(),
            time);
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    options.SerializerOptions.Converters.Add(converter);
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (context.Response.StatusCode, body);
    }

    public enum EnrollmentTokenState
    {
        Unknown,
        Expired,
        Consumed,
    }

    private sealed record RouteSetup(
        TestSqliteDatabase Database,
        CredentialStore Store,
        RecordingAuthAuditRecorder Audit,
        FakeTimeProvider Time) : IDisposable
    {
        public async Task<string> PrepareTokenAsync(EnrollmentTokenState state)
        {
            if (state == EnrollmentTokenState.Unknown)
                return "moh_enroll_unknown";

            var created = await Store.CreateEnrollmentTokenAsync(Time.GetUtcNow().AddMinutes(15));
            if (state == EnrollmentTokenState.Expired)
                Time.Advance(TimeSpan.FromMinutes(16));
            else
                await Store.ConsumeEnrollmentTokenAsync(
                    CredentialToken.Hash(created.Token), Time.GetUtcNow());
            return created.Token;
        }

        public void Dispose() => Database.Dispose();
    }

    private sealed class RecordingAuthAuditRecorder : IAuthAuditRecorder
    {
        public List<AuthAuditEvent> Events { get; } = [];

        public Task RecordAsync(AuthAuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
