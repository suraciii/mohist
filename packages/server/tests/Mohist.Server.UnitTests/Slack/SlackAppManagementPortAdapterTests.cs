using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAppManagementPortAdapterTests
{
    private const string EnrollmentId = "enrollment-1";
    private const string AgentAppId = "agent-app-1";
    private const string TeamId = "T123";

    [Fact]
    public async Task Create_posts_manifest_with_config_token_and_returns_identity_install_url_and_secrets()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"ok":true,"app_id":"A999","credentials":{"client_id":"C1","client_secret":"xoxc-1","signing_secret":"sig-1"},
             "permissions":{"bot":["chat:write","users:read"]}}
            """));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: """{"name":"Bot"}"""));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal("A999", result.AppId);
        Assert.Equal("https://api.slack.com/apps/A999/oauth", result.InstallUrl);
        Assert.Equal("xoxc-1", result.ClientSecret);
        Assert.Equal("sig-1", result.SigningSecret);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/apps.manifest.create", recorded.Uri);
        Assert.Equal("Bearer config-access", recorded.Authorization);
        Assert.Contains("manifest=", recorded.Body);
        Assert.DoesNotContain("config-access", recorded.Body);
    }

    [Fact]
    public async Task Create_without_manifest_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true,"app_id":"A1"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("manifest_required", result.ErrorClass);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_without_configuration_token_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true,"app_id":"A1"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore());

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("configuration_credential_missing", result.ErrorClass);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_slack_rejection_is_definite_failure_with_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"invalid_manifest"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("invalid_manifest", result.ErrorClass);
    }

    [Fact]
    public async Task Create_timeout_is_unknown()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.Unknown, result.Outcome);
        Assert.Equal("transport_error", result.ErrorClass);
    }

    [Fact]
    public async Task Create_success_without_credentials_keeps_secrets_null_and_install_url_from_app_id()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true,"app_id":"A7"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal("A7", result.AppId);
        Assert.Equal("https://api.slack.com/apps/A7/oauth", result.InstallUrl);
        Assert.Null(result.ClientSecret);
        Assert.Null(result.SigningSecret);
    }

    [Fact]
    public async Task Validate_posts_canonical_manifest_without_app_id()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));
        var manifest = new SlackManifest(2, """{"display_information":{"name":"Bot"}}""", "hash");

        var result = await adapter.ValidateManifestAsync(new(new(EnrollmentId, AgentAppId, TeamId), manifest));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/apps.manifest.validate", recorded.Uri);
        Assert.DoesNotContain("app_id=", recorded.Body);
        Assert.Contains("manifest=", recorded.Body);
    }

    [Fact]
    public async Task Update_posts_manifest_with_app_id()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));
        var manifest = new SlackManifest(2, "{}", "hash");

        var result = await adapter.UpdateManifestAsync(new(new(EnrollmentId, AgentAppId, TeamId, "A1"), manifest));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/apps.manifest.update", recorded.Uri);
        Assert.Contains("app_id=A1", recorded.Body);
    }

    [Fact]
    public async Task Update_without_app_id_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.UpdateManifestAsync(new(new(EnrollmentId, AgentAppId, TeamId), new SlackManifest(2, "{}", "hash")));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("app_id_required", result.ErrorClass);
    }

    [Fact]
    public async Task Export_returns_present_with_manifest_json()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true,"manifest":{"display_information":{"name":"Bot"}}}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.ExportManifestAsync(new(EnrollmentId, AgentAppId, TeamId, "A1"));

        Assert.Equal(SlackAppManagementFactOutcome.Present, result.Outcome);
        Assert.Contains("\"name\":\"Bot\"", result.ManifestJson);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/apps.manifest.export", recorded.Uri);
        Assert.Contains("app_id=A1", recorded.Body);
    }

    [Fact]
    public async Task Inspect_not_found_is_absent()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"app_not_found"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.InspectAsync(new(EnrollmentId, AgentAppId, TeamId, "A1"));

        Assert.Equal(SlackAppManagementFactOutcome.Absent, result.Outcome);
        Assert.Equal("app_not_found", result.ErrorClass);
    }

    [Fact]
    public async Task Inspect_auth_failure_is_unknown()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"invalid_config_token"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.InspectAsync(new(EnrollmentId, AgentAppId, TeamId, "A1"));

        Assert.Equal(SlackAppManagementFactOutcome.Unknown, result.Outcome);
        Assert.Equal("invalid_config_token", result.ErrorClass);
    }

    [Fact]
    public async Task Inspect_without_app_id_is_unknown()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.InspectAsync(new(EnrollmentId, AgentAppId, TeamId));

        Assert.Equal(SlackAppManagementFactOutcome.Unknown, result.Outcome);
        Assert.Equal("app_id_required", result.ErrorClass);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_posts_app_id_with_config_token()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.DeleteAsync(new(EnrollmentId, AgentAppId, TeamId, "A1"));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal("A1", result.AppId);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/apps.manifest.delete", recorded.Uri);
        Assert.Equal("Bearer config-access", recorded.Authorization);
        Assert.Contains("app_id=A1", recorded.Body);
    }

    [Fact]
    public async Task Delete_without_app_id_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(http), new FakeSecretStore("config-access"));

        var result = await adapter.DeleteAsync(new(EnrollmentId, AgentAppId, TeamId));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("app_id_required", result.ErrorClass);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly byte[]? _configurationToken;

        public FakeSecretStore(string? configurationToken = null) =>
            _configurationToken = configurationToken is null ? null : Encoding.UTF8.GetBytes(configurationToken);

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(address.Kind == SecretKind.ConfigurationAccessToken ? _configurationToken : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(false);

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(string Uri, string Body, string? Authorization);
}
