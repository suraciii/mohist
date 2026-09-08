using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Events.WebSocket;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;
using Mohist.Server.Webhooks;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L0")]
public sealed class EventSocketOriginTests
{
    [Theory]
    [InlineData("192.0.2.10", "192.0.2.10")]
    [InlineData("192.0.2.10", "::ffff:192.0.2.10")]
    [InlineData("::ffff:192.0.2.10", "192.0.2.10")]
    [InlineData("2001:db8::10", "2001:db8::10")]
    public async Task TrustedImmediateProxyAcceptsMatchingPublicOrigin(string trusted, string peer)
    {
        using var fixture = new OriginFixture(trusted, peer);

        await fixture.InvokeAsync();

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, fixture.Context.Response.StatusCode);
        Assert.Equal(1, fixture.Socket.AcceptCount);
        var response = Assert.Single(fixture.Socket.Responses);
        Assert.Equal("set", response.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, response.GetProperty("result").ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal(WebSocketState.Closed, fixture.Socket.State);
        Assert.Equal("http", fixture.Context.Request.Scheme);
        Assert.Equal("internal:3456", fixture.Context.Request.Host.Value);
        Assert.Equal(IPAddress.Parse(peer), fixture.Context.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("proxy.example.test")]
    [InlineData("192.0.2.0/24")]
    [InlineData("https://192.0.2.10")]
    [InlineData("not-an-address")]
    public void InvalidTrustedAddressFailsStartupValidation(string address)
    {
        using var fixture = new OriginFixture(address, "192.0.2.10");

        var error = Assert.Throws<OptionsValidationException>(() =>
            fixture.Services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("EventSocket:TrustedProxyAddresses", error.Message);
    }

    [Theory]
    [InlineData(null, "192.0.2.10", "https://mohist.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.11", "https://mohist.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "::ffff:192.0.2.11", "https://mohist.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", null, "https://mohist.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.11", "http://internal:3456", "not-https", "bad/host", true)]
    [InlineData(null, "127.0.0.1", "https://mohist.example.test", "https", "mohist.example.test", true)]
    [InlineData(null, "::1", "https://mohist.example.test", "https", "mohist.example.test", true)]
    [InlineData(null, "::ffff:127.0.0.1", "https://mohist.example.test", "https", "mohist.example.test", true)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", null, null, true)]
    [InlineData("192.0.2.10", "192.0.2.10", "https://mohist.example.test", null, null, false)]
    [InlineData("192.0.2.10", "192.0.2.10", "https://other.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://mohist.example.test", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", null, "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "https://mohist.example.test/path", "https", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", null, "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", null, false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", "", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https,http", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", "mohist.example.test,internal:3456", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", " https ", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "ftp", "mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", "user@mohist.example.test", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", "mohist.example.test/path", false)]
    [InlineData("192.0.2.10", "192.0.2.10", "http://internal:3456", "https", "mohist.example.test:invalid", false)]
    public async Task CookieOriginPreservesTheImmediatePeerBoundary(
        string? trusted, string? peer, string? origin, string? proto, string? host, bool accepted)
    {
        using var fixture = new OriginFixture(trusted, peer);
        SetHeader(fixture.Context.Request, "Origin", origin);
        SetHeader(fixture.Context.Request, "X-Forwarded-Proto", proto);
        SetHeader(fixture.Context.Request, "X-Forwarded-Host", host);

        await fixture.InvokeAsync();

        Assert.Equal(accepted ? StatusCodes.Status101SwitchingProtocols : StatusCodes.Status403Forbidden,
            fixture.Context.Response.StatusCode);
        Assert.Equal(accepted ? 1 : 0, fixture.Socket.AcceptCount);
        Assert.Equal("http", fixture.Context.Request.Scheme);
        Assert.Equal("internal:3456", fixture.Context.Request.Host.Value);
        Assert.Equal(peer is null ? null : IPAddress.Parse(peer), fixture.Context.Connection.RemoteIpAddress);
        if (!accepted) Assert.Empty(fixture.Socket.Responses);
    }

    [Theory]
    [InlineData("Origin")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-Host")]
    public async Task MultipleHeaderValuesRejectBeforeUpgrade(string header)
    {
        using var fixture = new OriginFixture("192.0.2.10", "192.0.2.10");
        var value = fixture.Context.Request.Headers[header][0]!;
        fixture.Context.Request.Headers[header] = new StringValues([value, value]);

        await fixture.InvokeAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, fixture.Context.Response.StatusCode);
        Assert.Equal(0, fixture.Socket.AcceptCount);
    }

    [Fact]
    public async Task BearerUpgradeDoesNotRequireOrigin()
    {
        using var fixture = new OriginFixture(null, "192.0.2.11");
        fixture.Context.Items[CredentialCarrierResolution.HttpContextItemKey] = CredentialCarrier.Bearer;
        fixture.Context.Request.Headers.Remove("Origin");

        await fixture.InvokeAsync();

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, fixture.Context.Response.StatusCode);
        Assert.Equal(1, fixture.Socket.AcceptCount);
        Assert.Equal(WebSocketState.Closed, fixture.Socket.State);
    }

    [Fact]
    public async Task TrustedAddressesAreFrozenBeforeTheFirstRequest()
    {
        using var fixture = new OriginFixture("192.0.2.10", "192.0.2.10");
        fixture.Services.GetRequiredService<IStartupValidator>().Validate();
        fixture.Configuration["EventSocket:TrustedProxyAddresses:0"] = "192.0.2.11";
        fixture.Configuration.Reload();

        await fixture.InvokeAsync();

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, fixture.Context.Response.StatusCode);
        Assert.Equal(1, fixture.Socket.AcceptCount);
    }

    private static void SetHeader(HttpRequest request, string name, string? value)
    {
        if (value is null) request.Headers.Remove(name);
        else request.Headers[name] = value;
    }

    private sealed class OriginFixture : IDisposable
    {
        public IConfigurationRoot Configuration { get; }
        public ServiceProvider Services { get; }
        public DefaultHttpContext Context { get; }
        public UpgradeSocket Socket { get; } = new();

        public OriginFixture(string? trusted, string? peer)
        {
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(
                trusted is null ? [] : new Dictionary<string, string?>
                {
                    ["EventSocket:TrustedProxyAddresses:0"] = trusted,
                }).Build();
            var services = new ServiceCollection();
            services.ConfigureMohistServices(Configuration);
            services.AddLogging();
            services.AddSingleton(new EventWebSocketRegistry(
                new WebhookPayloadRenderer(), new FakeTimeProvider(),
                NullLoggerFactory.Instance, NullEventMatchFailureSink.Instance));
            Services = services.BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = Services };
            Context.RequestAborted = TestContext.Current.CancellationToken;
            Context.Response.Body = new MemoryStream();
            Context.Request.Scheme = "http";
            Context.Request.Host = new HostString("internal", 3456);
            Context.Request.Headers.Origin = "https://mohist.example.test";
            Context.Request.Headers["X-Forwarded-Proto"] = "https";
            Context.Request.Headers["X-Forwarded-Host"] = "mohist.example.test";
            Context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            Context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
            Context.Items[CredentialCarrierResolution.HttpContextItemKey] = CredentialCarrier.Cookie;
            Context.Items[ProjectResolutionEndpointFilter.ProjectInfoItemKey] = new ProjectInfo
            {
                Id = "project-origin", Name = "origin", CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z",
            };
            Socket.Context = Context;
            Context.Features.Set<IHttpWebSocketFeature>(Socket);
        }

        public Task InvokeAsync()
        {
            var handler = typeof(ProjectEventSocketRoutes).GetMethod("HandleAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var endpoint = RequestDelegateFactory.Create(handler, targetFactory: null, new RequestDelegateFactoryOptions { ServiceProvider = Services });
            return endpoint.RequestDelegate(Context);
        }

        public void Dispose()
        {
            Socket.Dispose();
            Context.Response.Body.Dispose();
            Services.Dispose();
        }
    }

    private sealed class UpgradeSocket : System.Net.WebSockets.WebSocket, IHttpWebSocketFeature
    {
        private readonly TaskCompletionSource _responseSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _receives;
        public DefaultHttpContext Context { get; set; } = null!;
        public int AcceptCount { get; private set; }
        public List<JsonElement> Responses { get; } = [];
        public bool IsWebSocketRequest => true;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            AcceptCount++;
            Context.Response.StatusCode = StatusCodes.Status101SwitchingProtocols;
            return Task.FromResult<System.Net.WebSockets.WebSocket>(this);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_receives++ == 0)
            {
                var bytes = Encoding.UTF8.GetBytes("""
                    {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[]}}
                    """);
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new(bytes.Length, WebSocketMessageType.Text, true);
            }
            await _responseSent.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            return new(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Responses.Add(JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan()));
            _responseSent.TrySetResult();
            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
