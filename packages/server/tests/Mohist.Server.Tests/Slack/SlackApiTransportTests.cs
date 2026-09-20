using System.Net;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure.Slack.Ports;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackApiTransportTests
{
    [Fact]
    public async Task PostForm_encodes_form_and_bearer_token_for_relative_endpoint()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync(
            "auth.test",
            new Dictionary<string, string> { ["refresh_token"] = "r1" },
            "xoxb-token",
            CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("https://slack.test/api/auth.test", recorded.Uri);
        Assert.Equal("application/x-www-form-urlencoded", recorded.ContentType);
        Assert.Equal("refresh_token=r1", recorded.Body);
        Assert.Equal("Bearer xoxb-token", recorded.Authorization);
        Assert.Equal(SlackApiCallOutcome.Ok, response.Outcome);
        response.Body?.Dispose();
    }

    [Fact]
    public async Task PostForm_without_form_sends_no_content()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":true}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        await transport.PostFormAsync("auth.test", form: null, bearerToken: null, CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        Assert.Null(recorded.ContentType);
        Assert.Null(recorded.Authorization);
    }

    [Fact]
    public async Task PostForm_ok_false_is_rejected_with_slack_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false,"error":"invalid_auth"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.Rejected, response.Outcome);
        Assert.Equal("invalid_auth", response.Error);
    }

    [Fact]
    public async Task PostForm_ok_false_without_error_falls_back_to_unknown_error()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{"ok":false}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.Rejected, response.Outcome);
        Assert.Equal("unknown_error", response.Error);
    }

    [Fact]
    public async Task PostForm_non_ok_status_is_rejected_with_http_status_class()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.Rejected, response.Outcome);
        Assert.Equal("http_401", response.Error);
        Assert.Null(response.RetryAfter);
    }

    [Fact]
    public async Task PostForm_http_429_is_rate_limited_with_the_retry_after_delay()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "33");
            return response;
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.RateLimited, response.Outcome);
        Assert.Equal("ratelimited", response.Error);
        Assert.Equal(TimeSpan.FromSeconds(33), response.RetryAfter);
    }

    [Fact]
    public async Task PostForm_http_429_without_a_parseable_retry_after_still_reports_the_rate_limit()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "Wed, 21 Oct 2026 07:28:00 GMT");
            return response;
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.RateLimited, response.Outcome);
        Assert.Null(response.RetryAfter);
    }

    [Fact]
    public async Task PostForm_ok_false_ratelimited_is_rate_limited_with_the_retry_after_delay()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = JsonResponse("""{"ok":false,"error":"ratelimited"}""");
            response.Headers.Add("Retry-After", "5");
            return response;
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.RateLimited, response.Outcome);
        Assert.Equal("ratelimited", response.Error);
        Assert.Equal(TimeSpan.FromSeconds(5), response.RetryAfter);
    }

    [Fact]
    public async Task PostForm_non_ok_status_with_json_error_uses_the_slack_error_class()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"token_revoked"}""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.Rejected, response.Outcome);
        Assert.Equal("token_revoked", response.Error);
    }

    [Fact]
    public async Task PostForm_unparseable_body_is_unparseable()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.Unparseable, response.Outcome);
    }

    [Fact]
    public async Task PostForm_handler_timeout_is_transport_error()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.TransportError, response.Outcome);
    }

    [Fact]
    public async Task PostForm_network_failure_is_transport_error()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        var response = await transport.PostFormAsync("auth.test", null, null, CancellationToken.None);

        Assert.Equal(SlackApiCallOutcome.TransportError, response.Outcome);
    }

    [Fact]
    public async Task PostForm_caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHttpMessageHandler(_ => throw new OperationCanceledException(cts.Token));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://slack.test/api/") };
        var transport = new SlackApiTransport(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PostFormAsync("auth.test", null, null, cts.Token));
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
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Headers.Authorization?.ToString()));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? ContentType,
        string Body,
        string? Authorization);
}
