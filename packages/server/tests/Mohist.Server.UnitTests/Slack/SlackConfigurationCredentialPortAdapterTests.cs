using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackConfigurationCredentialPortAdapterTests
{
    // Slack tooling.tokens.rotate returns token/exp (epoch seconds), not
    // access_token/expires_in; tests must mock the real response shape.
    private const long ExpEpochSeconds = 1786060800;
    private static readonly DateTimeOffset ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(ExpEpochSeconds);

    [Fact]
    public async Task Rotate_posts_refresh_token_and_returns_rotated_pair_with_team_and_expiry()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            $$"""{"ok":true,"token":"next-access","refresh_token":"next-refresh","exp":{{ExpEpochSeconds}},"team_id":"T123"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        var result = await adapter.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Succeeded, result.Outcome);
        Assert.Equal(new SlackConfigurationCredentialPair("next-access", "next-refresh"), result.Credentials);
        Assert.Equal("T123", result.WorkspaceTeamId);
        Assert.Equal(ExpiresAt, result.ExpiresAt);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/tooling.tokens.rotate", recorded.Uri);
        Assert.Equal("refresh_token=refresh", recorded.Body);
    }

    [Fact]
    public async Task Rotate_missing_team_id_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            $$"""{"ok":true,"token":"a","refresh_token":"r","exp":{{ExpEpochSeconds}}}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        var result = await adapter.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("invalid_rotation_result", result.ErrorClass);
    }

    [Fact]
    public async Task Rotate_missing_exp_is_definite_failure()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """{"ok":true,"token":"a","refresh_token":"r","team_id":"T123"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        var result = await adapter.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("invalid_rotation_result", result.ErrorClass);
    }

    [Fact]
    public async Task Rotate_slack_rejection_is_definite_failure_with_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"invalid_refresh_token"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        var result = await adapter.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("invalid_refresh_token", result.ErrorClass);
    }

    [Fact]
    public async Task Rotate_timeout_is_unknown()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        var result = await adapter.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Unknown, result.Outcome);
        Assert.Equal("transport_error", result.ErrorClass);
    }

    [Fact]
    public async Task Rotate_rejects_blank_credentials()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var adapter = new SlackConfigurationCredentialPortAdapter(
            new SlackApiTransport(http));

        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.RotateAsync(new(string.Empty, "refresh")));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

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
                body));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(string Uri, string Body);
}
