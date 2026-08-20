using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Identity;
using Xunit;
using static Mohist.Server.UnitTests.Auth.AuthResolutionTestSupport;

namespace Mohist.Server.UnitTests.Auth;

/// <summary>
/// P2 scope enforcement inside <see cref="AuthResolutionMiddleware"/>:
/// route declarations (or the method-based default for business routes)
/// gate every authenticated request, insufficient scope answers 403 with
/// the principal, and runner credentials stay bound to their RunnerId —
/// any path or header self-declaring another runner is
/// rejected before the route runs.
/// </summary>
public sealed class AuthResolutionScopeTests
{
    private const string AdminToken = AuthResolutionTestSupport.AdminToken;

    [Fact]
    public async Task ReadonlyCredential_OnDefaultBusinessGet_Passes()
    {
        var (middleware, context) = NewReadonlyContext(path: "/api/projects");

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ReadonlyCredential_OnDefaultBusinessPost_Answers403()
    {
        var (middleware, context) = NewReadonlyContext(path: "/api/projects", method: HttpMethods.Post);

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
        var body = JsonDocument.Parse(ReadBody(context));
        Assert.Equal("forbidden", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "agent-readonly",
            body.RootElement.GetProperty("details").GetProperty("principal").GetString());
        Assert.Equal(
            "readonly",
            body.RootElement.GetProperty("details").GetProperty("granted")[0].GetString());
    }

    [Fact]
    public async Task ReadonlyCredential_OnSensitiveOperatorRoute_Answers403()
    {
        var (middleware, context) = NewReadonlyContext(
            path: "/api/fs/home",
            endpoint: new RouteScopeRequirement(RouteScopeRequirementExtensions.Operator));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
        var body = JsonDocument.Parse(ReadBody(context));
        Assert.Contains("operator", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            new[] { "operator" },
            body.RootElement.GetProperty("details").GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task ReadonlyCredential_OnReadonlyDeclaredRouteGet_Passes()
    {
        var (middleware, context) = NewReadonlyContext(
            path: "/hubs/events/negotiate",
            endpoint: new RouteScopeRequirement(RouteScopeRequirementExtensions.OperatorOrReadonly));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ReadonlyCredential_OnReadonlyDeclaredRoutePost_Answers403()
    {
        var (middleware, context) = NewReadonlyContext(
            path: "/otel/api/query",
            method: HttpMethods.Post,
            endpoint: new RouteScopeRequirement(RouteScopeRequirementExtensions.OperatorOrReadonly));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
    }

    [Fact]
    public async Task ReadonlyCredential_OnReadonlyDeclaredHubNegotiate_Passes()
    {
        // SignalR clients negotiate over POST; the handshake belongs to
        // the observation connection, so readonly is not blocked by it.
        var (middleware, context) = NewReadonlyContext(
            path: "/hubs/events/negotiate",
            method: HttpMethods.Post,
            endpoint: new RouteScopeRequirement(RouteScopeRequirementExtensions.OperatorOrReadonly));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task OperatorCredential_OnReadonlyDeclaredRoute_Passes()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {AdminToken}");
        SetEndpoint(context, new RouteScopeRequirement(RouteScopeRequirementExtensions.OperatorOrReadonly));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ReadonlyCredential_OnRunnerRoute_Answers403()
    {
        var (middleware, context) = NewReadonlyContext(
            path: "/api/runner/runner-a/heartbeat",
            method: HttpMethods.Post,
            endpoint: new RouteScopeRequirement(RouteScopeRequirementExtensions.Runner));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
    }

    [Fact]
    public async Task RunnerCredential_OnItsOwnRunnerPath_Passes()
    {
        var (middleware, context) = NewRunnerContext(path: "/api/runner/runner-a/heartbeat");

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task RunnerCredential_OnAnotherRunnersPath_Answers403()
    {
        var (middleware, context) = NewRunnerContext(path: "/api/runner/runner-b/heartbeat");

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
        var body = JsonDocument.Parse(ReadBody(context));
        Assert.Equal("runner-a", body.RootElement.GetProperty("details").GetProperty("boundRunnerId").GetString());
        Assert.Equal("runner-b", body.RootElement.GetProperty("details").GetProperty("claimedRunnerId").GetString());
    }

    [Fact]
    public async Task RunnerCredential_WithMismatchedRunnerHeader_Answers403()
    {
        var (middleware, context) = NewRunnerContext(
            path: "/api/workflow-runs/run-1/work/w-1/task-log",
            method: HttpMethods.Post);
        context.Request.Headers["x-mohist-runner-id"] = "runner-b";

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
    }

    [Fact]
    public async Task RunnerCredential_WithMatchingRunnerHeader_Passes()
    {
        var (middleware, context) = NewRunnerContext(
            path: "/api/workflow-runs/run-1/work/w-1/task-log",
            method: HttpMethods.Post);
        context.Request.Headers["x-mohist-runner-id"] = "runner-a";

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task RunnerCredential_OnBusinessRoute_Answers403()
    {
        // No scope metadata: the default business-route policy applies
        // (GET is operator-or-readonly), which a runner credential lacks.
        var (middleware, context) = NewRunnerContext(path: "/api/projects", runnerEndpoint: false);

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertForbidden(context);
    }

    [Fact]
    public async Task OperatorCredential_OnAnotherRunnersPath_Passes()
    {
        // Operator is not bound to a runner: it may act on any runner
        // path (admin operations), while runner credentials stay bound.
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {AdminToken}");
        context.Request.Path = "/api/runner/runner-b/heartbeat";
        context.Request.Method = HttpMethods.Post;
        SetEndpoint(context, new RouteScopeRequirement(RouteScopeRequirementExtensions.Runner));

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.True(invoked);
    }
}
