using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("SlackLeaseRoutes")]
public sealed class SlackAdapterLeaseRoutesSpecs
{
    private const string OperatorId = "operator-lease-routes";
    private const string TargetsPath = "/api/slack-adapter/leases/targets";
    private const string AcquirePath = "/api/slack-adapter/leases/acquire";
    private const string HelloPath = "/api/slack-adapter/leases/hello";
    private const string RenewPath = "/api/slack-adapter/leases/renew";

    private readonly SlackAdapterLeaseRoutesFixture _fixture;

    public SlackAdapterLeaseRoutesSpecs(SlackAdapterLeaseRoutesFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(TargetsPath)]
    [InlineData(AcquirePath)]
    [InlineData(HelloPath)]
    [InlineData(RenewPath)]
    public async Task Every_route_requires_the_operator_token_and_an_explicit_operator_id(string path)
    {
        var body = AcquireBody(new SlackLeaseTargetRef.Manager("enr_auth", "T_AUTH"), "adapter-A");
        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var tokenOnly = _fixture.CreateTokenOnlyClient();

        using var anonymousResponse = await SendAsync(anonymous, path, body);
        Assert.Equal(HttpStatusCode.Forbidden, anonymousResponse.StatusCode);
        Assert.Equal("operator_credential_required", await CodeAsync(anonymousResponse));

        using var tokenOnlyResponse = await SendAsync(tokenOnly, path, body);
        Assert.Equal(HttpStatusCode.Forbidden, tokenOnlyResponse.StatusCode);
        Assert.Equal("operator_credential_required", await CodeAsync(tokenOnlyResponse));
    }

    [Fact]
    public async Task Discovery_returns_only_app_token_targets_and_never_secrets()
    {
        var manager = new SlackLeaseTargetRef.Manager(NewId("enr"), "T_DISCOVER");
        _fixture.Targets
            .Add(Target(manager, "A_DISCOVER", active: true, appToken: true, botToken: true, verified: false))
            .Add(Target(new SlackLeaseTargetRef.Connection(NewId("proj"), NewId("conn")), "A_OTHER",
                active: true, appToken: false, botToken: true, verified: false));

        using var client = _fixture.CreateOperatorClient(OperatorId);
        using var response = await client.GetAsync(TargetsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        var view = data.EnumerateArray().Single(item =>
            item.GetProperty("expectedAppId").GetString() == "A_DISCOVER");
        Assert.Equal(SlackLeaseTargetKind.Manager, view.GetProperty("kind").GetString());
        Assert.Equal(manager.EnrollmentId, view.GetProperty("enrollmentId").GetString());
        Assert.Equal(manager.WorkspaceTeamId, view.GetProperty("workspaceTeamId").GetString());
        Assert.True(view.GetProperty("active").GetBoolean());
        Assert.True(view.GetProperty("appLevelTokenProvisioned").GetBoolean());
        Assert.True(view.GetProperty("botTokenProvisioned").GetBoolean());
        Assert.False(view.GetProperty("credentialVerified").GetBoolean());
        Assert.True(view.GetProperty("canAcquireValidation").GetBoolean());
        Assert.False(view.GetProperty("canAcquireRuntime").GetBoolean());
        AssertNoSecretProperties(data);
    }

    [Fact]
    public async Task Validation_acquire_returns_the_app_token_and_no_bot_token()
    {
        var manager = new SlackLeaseTargetRef.Manager(NewId("enr"), "T_VALIDATE");
        _fixture.Targets.Add(Target(manager, "A_VALIDATE", active: true, appToken: true, botToken: true, verified: false));
        _fixture.Secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");

        using var client = _fixture.CreateOperatorClient(OperatorId);
        using var response = await client.PostAsJsonAsync(AcquirePath, AcquireBody(manager, "adapter-A"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("leaseId").GetString()));
        Assert.Equal("A_VALIDATE", data.GetProperty("expectedAppId").GetString());
        Assert.Equal("xapp-candidate", data.GetProperty("appToken").GetString());
        Assert.False(data.TryGetProperty("botToken", out _));
    }

    [Fact]
    public async Task Runtime_flow_acquire_hello_then_runtime_lease_with_both_tokens()
    {
        var manager = new SlackLeaseTargetRef.Manager(NewId("enr"), "T_RUNTIME");
        _fixture.Targets.Add(Target(manager, "A_RUNTIME", active: true, appToken: true, botToken: true, verified: false));
        _fixture.Secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        _fixture.Secrets.Put(manager, SecretKind.BotToken, "xoxb-live");

        using var client = _fixture.CreateOperatorClient(OperatorId);
        var validation = await DataAsync(await client.PostAsJsonAsync(AcquirePath, AcquireBody(manager, "adapter-A")));

        using var helloResponse = await client.PostAsJsonAsync(HelloPath, new
        {
            target = TargetBody(manager),
            leaseId = validation.GetProperty("leaseId").GetString(),
            appId = "A_RUNTIME",
        });
        Assert.Equal(HttpStatusCode.OK, helloResponse.StatusCode);
        Assert.Equal("verified", (await DataAsync(helloResponse)).GetProperty("outcome").GetString());

        using var runtimeResponse = await client.PostAsJsonAsync(AcquirePath, new
        {
            kind = SlackLeaseKind.Runtime,
            target = TargetBody(manager),
            adapterId = "adapter-A",
        });
        Assert.Equal(HttpStatusCode.OK, runtimeResponse.StatusCode);
        var runtime = await DataAsync(runtimeResponse);
        Assert.Equal("xapp-live", runtime.GetProperty("appToken").GetString());
        Assert.Equal("xoxb-live", runtime.GetProperty("botToken").GetString());

        // A verified target can no longer acquire a validation lease.
        using var refused = await client.PostAsJsonAsync(AcquirePath, AcquireBody(manager, "adapter-A"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("lease_not_acquirable", await CodeAsync(refused));
    }

    [Fact]
    public async Task Hello_mismatch_and_stale_leases_are_deterministic()
    {
        var manager = new SlackLeaseTargetRef.Manager(NewId("enr"), "T_HELLO");
        _fixture.Targets.Add(Target(manager, "A_HELLO", active: true, appToken: true, botToken: true, verified: false));
        _fixture.Secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");

        using var client = _fixture.CreateOperatorClient(OperatorId);
        var leaseId = (await DataAsync(
            await client.PostAsJsonAsync(AcquirePath, AcquireBody(manager, "adapter-A"))))
            .GetProperty("leaseId").GetString();

        using var mismatch = await client.PostAsJsonAsync(HelloPath, new
        {
            target = TargetBody(manager),
            leaseId,
            appId = "A_WRONG",
        });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("app_id_mismatch", await CodeAsync(mismatch));

        using var unknown = await client.PostAsJsonAsync(HelloPath, new
        {
            target = TargetBody(manager),
            leaseId = "lease_unknown",
            appId = "A_HELLO",
        });
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(unknown));

        using var verified = await client.PostAsJsonAsync(HelloPath, new
        {
            target = TargetBody(manager),
            leaseId,
            appId = "A_HELLO",
        });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        // The confirmed validation lease is fenced: a second hello is stale.
        using var replayed = await client.PostAsJsonAsync(HelloPath, new
        {
            target = TargetBody(manager),
            leaseId,
            appId = "A_HELLO",
        });
        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(replayed));
    }

    [Fact]
    public async Task Renew_extends_the_active_lease_and_rejects_stale_and_expired()
    {
        var manager = new SlackLeaseTargetRef.Manager(NewId("enr"), "T_RENEW");
        _fixture.Targets.Add(Target(manager, "A_RENEW", active: true, appToken: true, botToken: true, verified: true));
        _fixture.Secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        _fixture.Secrets.Put(manager, SecretKind.BotToken, "xoxb-live");

        using var client = _fixture.CreateOperatorClient(OperatorId);
        var runtime = await DataAsync(await client.PostAsJsonAsync(AcquirePath, new
        {
            kind = SlackLeaseKind.Runtime,
            target = TargetBody(manager),
            adapterId = "adapter-A",
        }));
        var leaseId = runtime.GetProperty("leaseId").GetString();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        using var renewedResponse = await client.PostAsJsonAsync(RenewPath, new
        {
            target = TargetBody(manager),
            leaseId,
            adapterId = "adapter-A",
        });
        Assert.Equal(HttpStatusCode.OK, renewedResponse.StatusCode);
        var renewed = await DataAsync(renewedResponse);
        Assert.Equal(leaseId, renewed.GetProperty("leaseId").GetString());
        Assert.Equal(SlackLeaseKind.Runtime, renewed.GetProperty("kind").GetString());
        Assert.Equal(runtime.GetProperty("generation").GetInt32(), renewed.GetProperty("generation").GetInt32());
        Assert.True(renewed.GetProperty("expiresAt").GetDateTimeOffset() > runtime.GetProperty("expiresAt").GetDateTimeOffset());

        using var wrongAdapter = await client.PostAsJsonAsync(RenewPath, new
        {
            target = TargetBody(manager),
            leaseId,
            adapterId = "adapter-B",
        });
        Assert.Equal(HttpStatusCode.Conflict, wrongAdapter.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(wrongAdapter));

        _fixture.TimeProvider.Advance(SlackAdapterLeaseService.RuntimeLeaseTtl + TimeSpan.FromSeconds(1));
        using var expired = await client.PostAsJsonAsync(RenewPath, new
        {
            target = TargetBody(manager),
            leaseId,
            adapterId = "adapter-A",
        });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(expired));

        using var superseded = await client.PostAsJsonAsync(AcquirePath, new
        {
            kind = SlackLeaseKind.Runtime,
            target = TargetBody(manager),
            adapterId = "adapter-B",
        });
        Assert.Equal(HttpStatusCode.OK, superseded.StatusCode);
        using var staleRenew = await client.PostAsJsonAsync(RenewPath, new
        {
            target = TargetBody(manager),
            leaseId,
            adapterId = "adapter-A",
        });
        Assert.Equal(HttpStatusCode.Conflict, staleRenew.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleRenew));
    }

    [Theory]
    [InlineData("bogus", SlackLeaseTargetKind.Manager, "invalid_lease_kind")]
    [InlineData(SlackLeaseKind.Validation, "bogus", "invalid_target")]
    public async Task Acquire_rejects_unknown_kinds_and_malformed_targets(string kind, string targetKind, string expectedCode)
    {
        using var client = _fixture.CreateOperatorClient(OperatorId);

        using var response = await client.PostAsJsonAsync(AcquirePath, new
        {
            kind,
            target = new { kind = targetKind, enrollmentId = "enr_bad", workspaceTeamId = "T_BAD" },
            adapterId = "adapter-A",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await CodeAsync(response));
    }

    [Fact]
    public async Task Acquire_and_hello_require_adapter_id_lease_id_and_app_id()
    {
        using var client = _fixture.CreateOperatorClient(OperatorId);

        using var noAdapter = await client.PostAsJsonAsync(AcquirePath, new
        {
            kind = SlackLeaseKind.Validation,
            target = new { kind = SlackLeaseTargetKind.Manager, enrollmentId = "enr_missing", workspaceTeamId = "T_MISSING" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noAdapter.StatusCode);

        using var noLease = await client.PostAsJsonAsync(HelloPath, new
        {
            target = new { kind = SlackLeaseTargetKind.Manager, enrollmentId = "enr_missing", workspaceTeamId = "T_MISSING" },
            appId = "A_MISSING",
        });
        Assert.Equal(HttpStatusCode.BadRequest, noLease.StatusCode);

        using var noApp = await client.PostAsJsonAsync(HelloPath, new
        {
            target = new { kind = SlackLeaseTargetKind.Manager, enrollmentId = "enr_missing", workspaceTeamId = "T_MISSING" },
            leaseId = "lease_missing",
        });
        Assert.Equal(HttpStatusCode.BadRequest, noApp.StatusCode);
    }

    [Theory]
    [InlineData(AcquirePath)]
    [InlineData(HelloPath)]
    [InlineData(RenewPath)]
    public async Task Post_routes_reject_a_missing_or_null_target_without_server_error(string path)
    {
        using var client = _fixture.CreateOperatorClient(OperatorId);

        using var missing = await client.PostAsJsonAsync(path, new
        {
            kind = SlackLeaseKind.Validation,
            adapterId = "adapter-A",
            leaseId = "lease_x",
            appId = "A_X",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_target", await CodeAsync(missing));

        using var nullTarget = await client.PostAsJsonAsync(path, new
        {
            target = (object?)null,
            kind = SlackLeaseKind.Validation,
            adapterId = "adapter-A",
            leaseId = "lease_x",
            appId = "A_X",
        });
        Assert.Equal(HttpStatusCode.BadRequest, nullTarget.StatusCode);
        Assert.Equal("invalid_target", await CodeAsync(nullTarget));
    }

    private static object AcquireBody(SlackLeaseTargetRef target, string adapterId) => new
    {
        kind = SlackLeaseKind.Validation,
        target = TargetBody(target),
        adapterId,
    };

    private static object TargetBody(SlackLeaseTargetRef target) => target switch
    {
        SlackLeaseTargetRef.Manager manager => new
        {
            kind = SlackLeaseTargetKind.Manager,
            enrollmentId = manager.EnrollmentId,
            workspaceTeamId = manager.WorkspaceTeamId,
        },
        SlackLeaseTargetRef.Connection connection => new
        {
            kind = SlackLeaseTargetKind.Connection,
            projectId = connection.ProjectId,
            connectionId = connection.ConnectionId,
        },
        _ => throw new InvalidOperationException("Unsupported lease target ref."),
    };

    private static SlackLeaseTarget Target(
        SlackLeaseTargetRef @ref, string appId, bool active, bool appToken, bool botToken, bool verified) =>
        new(@ref, appId, active, appToken, botToken, verified,
            SecretStoreAddressFor(@ref, SecretKind.AppToken),
            SecretStoreAddressFor(@ref, SecretKind.BotToken));

    private static SecretStoreAddress SecretStoreAddressFor(SlackLeaseTargetRef @ref, SecretKind kind) =>
        @ref switch
        {
            SlackLeaseTargetRef.Manager manager =>
                SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, kind),
            SlackLeaseTargetRef.Connection connection =>
                SecretStoreAddress.ForAgentConnection(connection.ProjectId, connection.ConnectionId, kind),
            _ => throw new InvalidOperationException("Unsupported lease target ref."),
        };

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body) =>
        path == TargetsPath ? await client.GetAsync(TargetsPath) : await client.PostAsJsonAsync(path, body);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static readonly string[] ForbiddenPropertyNames =
        ["appToken", "botToken", "appLevelTokenAddress", "botTokenAddress", "token", "secret"];

    private static void AssertNoSecretProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                AssertNoSecretProperties(child);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in element.EnumerateObject())
        {
            Assert.DoesNotContain(
                property.Name, ForbiddenPropertyNames, StringComparer.OrdinalIgnoreCase);
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                AssertNoSecretProperties(property.Value);
        }
    }
}
