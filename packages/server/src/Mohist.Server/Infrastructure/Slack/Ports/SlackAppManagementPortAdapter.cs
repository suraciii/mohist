using System.Text.Json;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackAppManagementPort"/> / <see cref="ISlackAppManagementFactPort"/>:
/// drives the Slack apps.manifest endpoints with the enrollment's Configuration
/// access token, which is resolved from the secret store by enrollment id — the
/// port contract never carries tokens. Outcomes map Slack's ok/error envelope
/// onto Succeeded / DefiniteFailure / Unknown; transport failures and unparseable
/// responses are Unknown because the external side effect is uncertain.
/// </summary>
public sealed class SlackAppManagementPortAdapter(
    SlackApiTransport transport,
    ISecretStore secrets) : ISlackAppManagementPort, ISlackAppManagementFactPort
{
    public const string ValidateEndpoint = "apps.manifest.validate";
    public const string CreateEndpoint = "apps.manifest.create";
    public const string UpdateEndpoint = "apps.manifest.update";
    public const string ExportEndpoint = "apps.manifest.export";
    public const string DeleteEndpoint = "apps.manifest.delete";

    public const string ConfigurationCredentialMissingError = "configuration_credential_missing";
    public const string ManifestRequiredError = "manifest_required";
    public const string AppIdRequiredError = "app_id_required";

    public Task<SlackAppManagementResult> ValidateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default) =>
        SendManifestCallAsync(ValidateEndpoint, request, withAppId: false, ct);

    public Task<SlackAppManagementResult> UpdateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default) =>
        SendManifestCallAsync(UpdateEndpoint, request, withAppId: true, ct);

    public async Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ManifestJson))
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: ManifestRequiredError);

        var token = await ResolveConfigurationTokenAsync(request, ct).ConfigureAwait(false);
        if (token is null)
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: ConfigurationCredentialMissingError);

        var response = await transport.PostFormAsync(
            CreateEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifest"] = request.ManifestJson,
            },
            token,
            ct).ConfigureAwait(false);
        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => ParseCreated(response.Body),
            SlackApiCallOutcome.Rejected => new SlackAppManagementResult(
                SlackAppManagementOutcome.DefiniteFailure, ErrorClass: response.Error ?? "create_rejected"),
            SlackApiCallOutcome.Unparseable => new SlackAppManagementResult(
                SlackAppManagementOutcome.Unknown, ErrorClass: "unparseable_response"),
            _ => new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "transport_error"),
        };
    }

    public async Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: AppIdRequiredError);

        var token = await ResolveConfigurationTokenAsync(request, ct).ConfigureAwait(false);
        if (token is null)
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: ConfigurationCredentialMissingError);

        var response = await transport.PostFormAsync(
            DeleteEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            token,
            ct).ConfigureAwait(false);
        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, request.AppId),
            SlackApiCallOutcome.Rejected => new SlackAppManagementResult(
                SlackAppManagementOutcome.DefiniteFailure, ErrorClass: response.Error ?? "delete_rejected"),
            SlackApiCallOutcome.Unparseable => new SlackAppManagementResult(
                SlackAppManagementOutcome.Unknown, ErrorClass: "unparseable_response"),
            _ => new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "transport_error"),
        };
    }

    public async Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
            return new SlackAppManagementFact(SlackAppManagementFactOutcome.Unknown, ErrorClass: AppIdRequiredError);

        var token = await ResolveConfigurationTokenAsync(request, ct).ConfigureAwait(false);
        if (token is null)
            return new SlackAppManagementFact(SlackAppManagementFactOutcome.Unknown, ErrorClass: ConfigurationCredentialMissingError);

        var response = await transport.PostFormAsync(
            ExportEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            token,
            ct).ConfigureAwait(false);
        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => new SlackAppManagementFact(SlackAppManagementFactOutcome.Present, request.AppId),
            SlackApiCallOutcome.Rejected when response.Error is "not_found" or "app_not_found" or "invalid_app_id" =>
                new SlackAppManagementFact(SlackAppManagementFactOutcome.Absent, ErrorClass: response.Error),
            SlackApiCallOutcome.Rejected => new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Unknown, ErrorClass: response.Error ?? "inspect_rejected"),
            _ => new SlackAppManagementFact(SlackAppManagementFactOutcome.Unknown, ErrorClass: "transport_error"),
        };
    }

    public async Task<SlackAppManifestExport> ExportManifestAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
            return new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: AppIdRequiredError);

        var token = await ResolveConfigurationTokenAsync(request, ct).ConfigureAwait(false);
        if (token is null)
            return new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: ConfigurationCredentialMissingError);

        var response = await transport.PostFormAsync(
            ExportEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            token,
            ct).ConfigureAwait(false);
        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => ParseExportedManifest(response.Body),
            SlackApiCallOutcome.Rejected when response.Error is "not_found" or "app_not_found" or "invalid_app_id" =>
                new SlackAppManifestExport(SlackAppManagementFactOutcome.Absent, ErrorClass: response.Error),
            SlackApiCallOutcome.Rejected => new SlackAppManifestExport(
                SlackAppManagementFactOutcome.Unknown, ErrorClass: response.Error ?? "export_rejected"),
            _ => new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: "transport_error"),
        };
    }

    private async Task<SlackAppManagementResult> SendManifestCallAsync(
        string endpoint,
        SlackAppManifestRequest request,
        bool withAppId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (withAppId && string.IsNullOrWhiteSpace(request.App.AppId))
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: AppIdRequiredError);

        var token = await ResolveConfigurationTokenAsync(request.App, ct).ConfigureAwait(false);
        if (token is null)
            return new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: ConfigurationCredentialMissingError);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest"] = request.Manifest.CanonicalJson,
        };
        if (withAppId)
            form["app_id"] = request.App.AppId!;

        var response = await transport.PostFormAsync(endpoint, form, token, ct).ConfigureAwait(false);
        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded, request.App.AppId),
            SlackApiCallOutcome.Rejected => new SlackAppManagementResult(
                SlackAppManagementOutcome.DefiniteFailure, ErrorClass: response.Error ?? "manifest_rejected"),
            SlackApiCallOutcome.Unparseable => new SlackAppManagementResult(
                SlackAppManagementOutcome.Unknown, ErrorClass: "unparseable_response"),
            _ => new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "transport_error"),
        };
    }

    private async Task<string?> ResolveConfigurationTokenAsync(SlackAppManagementRequest request, CancellationToken ct)
    {
        var bytes = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(request.EnrollmentId, SecretKind.ConfigurationAccessToken),
            ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            return null;
        var token = System.Text.Encoding.UTF8.GetString(bytes);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static SlackAppManagementResult ParseCreated(JsonDocument? body)
    {
        if (body is null)
            return new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            var appId = ReadString(root, "app_id");
            if (appId is null)
                return new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "missing_app_id");

            string? clientId = null;
            string? clientSecret = null;
            string? signingSecret = null;
            string[] scopes = [];
            if (root.TryGetProperty("credentials", out var credentials)
                && credentials.ValueKind == JsonValueKind.Object)
            {
                clientId = ReadString(credentials, "client_id");
                clientSecret = ReadString(credentials, "client_secret");
                signingSecret = ReadString(credentials, "signing_secret");
            }
            if (root.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("bot", out var bot)
                && bot.ValueKind == JsonValueKind.Array)
            {
                scopes = bot.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()!)
                    .ToArray();
            }

            var installUrl = string.IsNullOrWhiteSpace(clientId)
                ? null
                : BuildInstallUrl(clientId, scopes);
            return new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded,
                appId,
                installUrl,
                ClientSecret: clientSecret,
                SigningSecret: signingSecret);
        }
    }

    private static SlackAppManifestExport ParseExportedManifest(JsonDocument? body)
    {
        if (body is null)
            return new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: "unparseable_response");
        using (body)
        {
            if (!body.RootElement.TryGetProperty("manifest", out var manifest))
                return new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: "missing_manifest");
            var manifestJson = manifest.ValueKind == JsonValueKind.String
                ? manifest.GetString()
                : JsonSerializer.Serialize(manifest);
            return string.IsNullOrWhiteSpace(manifestJson)
                ? new SlackAppManifestExport(SlackAppManagementFactOutcome.Unknown, ErrorClass: "missing_manifest")
                : new SlackAppManifestExport(SlackAppManagementFactOutcome.Present, manifestJson);
        }
    }

    private static string BuildInstallUrl(string clientId, IReadOnlyCollection<string> scopes) =>
        $"https://slack.com/oauth/v2/authorize?client_id={Uri.EscapeDataString(clientId)}"
        + (scopes.Count > 0 ? $"&scope={Uri.EscapeDataString(string.Join(",", scopes))}" : string.Empty);

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
