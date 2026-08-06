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
///
/// An auth-failure rejection (expired or revoked Configuration access token) is
/// retried once after rotating the pair through the rotation service, which
/// atomically persists the new pair; the retry stays invisible to callers. If
/// rotation also fails, the call degrades with the unique next action instead of
/// surfacing a bare Slack error — a fresh pair can only come from `mo slack setup`.
/// </summary>
public sealed class SlackAppManagementPortAdapter(
    SlackApiTransport transport,
    ISecretStore secrets,
    SlackConfigurationCredentialRotationService? rotations = null)
    : ISlackAppManagementPort, ISlackAppManagementFactPort
{
    public const string ValidateEndpoint = "apps.manifest.validate";
    public const string CreateEndpoint = "apps.manifest.create";
    public const string UpdateEndpoint = "apps.manifest.update";
    public const string ExportEndpoint = "apps.manifest.export";
    public const string DeleteEndpoint = "apps.manifest.delete";

    public const string ConfigurationCredentialMissingError = "configuration_credential_missing";
    public const string ConfigurationCredentialDegradedError = "configuration_credential_degraded";
    public const string ConfigurationCredentialRotationUnknownError = "credential-rotation-unknown";
    public const string ManifestRequiredError = "manifest_required";
    public const string AppIdRequiredError = "app_id_required";

    private const string ConfigurationCredentialNextAction =
        "The Slack Configuration access token could not be rotated; re-run `mo slack setup` to re-supply the Configuration access token pair.";

    private static readonly string[] ConfigurationCredentialFailureErrors =
        ["invalid_auth", "invalid_config_token"];

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

        var call = await SendWithReactiveRotationAsync(
            CreateEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifest"] = request.ManifestJson,
            },
            request,
            token,
            ct).ConfigureAwait(false);
        if (call.Degradation is { } degradation)
            return degradation.ToManagementResult();
        var response = call.Response!;
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

        var call = await SendWithReactiveRotationAsync(
            DeleteEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            request,
            token,
            ct).ConfigureAwait(false);
        if (call.Degradation is { } degradation)
            return degradation.ToManagementResult();
        var response = call.Response!;
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

        var call = await SendWithReactiveRotationAsync(
            ExportEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            request,
            token,
            ct).ConfigureAwait(false);
        if (call.Degradation is { } degradation)
            return degradation.ToFactResult();
        var response = call.Response!;
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

        var call = await SendWithReactiveRotationAsync(
            ExportEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_id"] = request.AppId,
            },
            request,
            token,
            ct).ConfigureAwait(false);
        if (call.Degradation is { } degradation)
            return degradation.ToExportResult();
        var response = call.Response!;
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

        var call = await SendWithReactiveRotationAsync(endpoint, form, request.App, token, ct).ConfigureAwait(false);
        if (call.Degradation is { } degradation)
            return degradation.ToManagementResult();
        var response = call.Response!;
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

    private async Task<ReactiveCall> SendWithReactiveRotationAsync(
        string endpoint,
        IReadOnlyDictionary<string, string> form,
        SlackAppManagementRequest request,
        string token,
        CancellationToken ct)
    {
        var response = await transport.PostFormAsync(endpoint, form, token, ct).ConfigureAwait(false);
        if (rotations is null
            || response.Outcome != SlackApiCallOutcome.Rejected
            || !IsConfigurationCredentialFailure(response.Error))
            return new ReactiveCall(response);

        var degradation = await RotateConfigurationCredentialsAsync(request, ct).ConfigureAwait(false);
        if (degradation is not null)
            return ReactiveCall.Degraded(degradation);

        // The rotation service persisted the new pair atomically; re-resolve it
        // from the store so the retry can never carry a token that was not saved.
        var rotatedToken = await ResolveConfigurationTokenAsync(request, ct).ConfigureAwait(false);
        if (rotatedToken is null || string.Equals(rotatedToken, token, StringComparison.Ordinal))
            return ReactiveCall.Degraded(SlackConfigurationCredentialDegradation.RefreshInvalid);

        response = await transport.PostFormAsync(endpoint, form, rotatedToken, ct).ConfigureAwait(false);
        if (response.Outcome == SlackApiCallOutcome.Rejected
            && IsConfigurationCredentialFailure(response.Error))
            return ReactiveCall.Degraded(SlackConfigurationCredentialDegradation.RefreshInvalid);
        return new ReactiveCall(response);
    }

    private async Task<SlackConfigurationCredentialDegradation?> RotateConfigurationCredentialsAsync(
        SlackAppManagementRequest request,
        CancellationToken ct)
    {
        var access = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(request.EnrollmentId, SecretKind.ConfigurationAccessToken),
            ct).ConfigureAwait(false);
        var refresh = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(request.EnrollmentId, SecretKind.ConfigurationRefreshToken),
            ct).ConfigureAwait(false);
        if (access is not { Length: > 0 } || refresh is not { Length: > 0 })
            return SlackConfigurationCredentialDegradation.RefreshInvalid;

        var rotation = await rotations!.RotateAsync(
            request.EnrollmentId,
            new SlackConfigurationCredentialPair(
                System.Text.Encoding.UTF8.GetString(access),
                System.Text.Encoding.UTF8.GetString(refresh)),
            ct).ConfigureAwait(false);
        return rotation.Outcome switch
        {
            SlackConfigurationCredentialRotationOutcome.Succeeded => null,
            SlackConfigurationCredentialRotationOutcome.Unknown => SlackConfigurationCredentialDegradation.RotationUnknown,
            _ => SlackConfigurationCredentialDegradation.RefreshInvalid,
        };
    }

    private static bool IsConfigurationCredentialFailure(string? error) =>
        error is not null && ConfigurationCredentialFailureErrors.Contains(error, StringComparer.Ordinal);

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

            // The App settings install page is the reliable install entry for an
            // existing App: an oauth authorize link requires a configured
            // redirect_urls entry that this self-hosted deployment cannot
            // promise, and Slack rejects the link without one.
            var installUrl = $"https://api.slack.com/apps/{appId}/oauth";
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

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private sealed record ReactiveCall(SlackApiResponse? Response = null, SlackConfigurationCredentialDegradation? Degradation = null)
    {
        public static ReactiveCall Degraded(SlackConfigurationCredentialDegradation degradation) =>
            new(Degradation: degradation);
    }

    private sealed record SlackConfigurationCredentialDegradation(
        SlackAppManagementOutcome ManagementOutcome,
        string ErrorClass,
        string NextAction)
    {
        public static SlackConfigurationCredentialDegradation RefreshInvalid { get; } = new(
            SlackAppManagementOutcome.DefiniteFailure,
            ConfigurationCredentialDegradedError,
            ConfigurationCredentialNextAction);

        public static SlackConfigurationCredentialDegradation RotationUnknown { get; } = new(
            SlackAppManagementOutcome.Unknown,
            ConfigurationCredentialRotationUnknownError,
            ConfigurationCredentialNextAction);

        public SlackAppManagementResult ToManagementResult() =>
            new(ManagementOutcome, ErrorClass: ErrorClass, ErrorMessage: NextAction);

        public SlackAppManagementFact ToFactResult() =>
            new(SlackAppManagementFactOutcome.Unknown, ErrorClass: ErrorClass, ErrorMessage: NextAction);

        public SlackAppManifestExport ToExportResult() =>
            new(SlackAppManagementFactOutcome.Unknown, ErrorClass: ErrorClass, ErrorMessage: NextAction);
    }
}
