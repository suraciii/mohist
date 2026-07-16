using EnvironmentAbstractions.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Mohist.Server.Infrastructure.Security;
using Xunit;

namespace Mohist.Server.UnitTests.Security;

public sealed class OperatorCredentialTests
{
    private const string Token =
        "test-operator-token-0123456789abcdef";

    [Fact]
    public void AuthorizesOnlyOneExactCredentialValue()
    {
        var configuration = ConfigurationWith(
            "Mohist:OperatorToken",
            Token);
        var credential = new OperatorCredential(
            configuration,
            EmptyEnvironment());
        var context = new DefaultHttpContext();

        Assert.False(
            credential.Authorizes(
                context.Request.Headers));

        context.Request.Headers[
            OperatorCredential.HeaderName] =
            "wrong-operator-token-0123456789abcdef";
        Assert.False(
            credential.Authorizes(
                context.Request.Headers));

        context.Request.Headers[
            OperatorCredential.HeaderName] = Token;
        Assert.True(
            credential.Authorizes(
                context.Request.Headers));

        context.Request.Headers[
            OperatorCredential.HeaderName] =
            new StringValues([Token, Token]);
        Assert.False(
            credential.Authorizes(
                context.Request.Headers));
    }

    [Fact]
    public void DefaultCredential_IsCreatedOnceAndReused()
    {
        const string home =
            "/mohist-tests/operator/default";
        var environment = EmptyEnvironment();
        environment["HOME"] = home;
        var store = new InMemoryCredentialStore();
        var configuration =
            new ConfigurationBuilder().Build();

        var first = new OperatorCredential(
            configuration,
            environment,
            store);
        var path = Path.Combine(
            home,
            ".mohist",
            "operator-token");
        var token = store.ReadExplicit(path).Trim();
        Assert.True(token.Length >= 32);

        var second = new OperatorCredential(
            configuration,
            environment,
            store);
        var context = new DefaultHttpContext();
        context.Request.Headers[
            OperatorCredential.HeaderName] = token;

        Assert.True(
            first.Authorizes(context.Request.Headers));
        Assert.True(
            second.Authorizes(context.Request.Headers));
        Assert.Equal(1, store.CreateCount);
    }

    [Fact]
    public void EnvironmentCredentialOverridesConfigCredential()
    {
        var environment = EmptyEnvironment();
        environment[
            OperatorCredential.TokenEnvironmentVariable] =
            "environment-operator-token-0123456789abcdef";
        var configuration = ConfigurationWith(
            "Mohist:OperatorToken",
            "configuration-operator-token-0123456789abcdef");
        var credential = new OperatorCredential(
            configuration,
            environment);
        var context = new DefaultHttpContext();

        context.Request.Headers[
            OperatorCredential.HeaderName] =
            "configuration-operator-token-0123456789abcdef";
        Assert.False(
            credential.Authorizes(
                context.Request.Headers));

        context.Request.Headers[
            OperatorCredential.HeaderName] =
            "environment-operator-token-0123456789abcdef";
        Assert.True(
            credential.Authorizes(
                context.Request.Headers));
    }

    [Fact]
    public void ExplicitCredentialPath_ReadsManagedToken()
    {
        const string path =
            "/mohist-tests/operator/explicit/token";
        var store = new InMemoryCredentialStore();
        store.Set(path, Token);
        var configuration = ConfigurationWith(
            "Mohist:OperatorTokenPath",
            path);

        var credential = new OperatorCredential(
            configuration,
            EmptyEnvironment(),
            store);
        var context = new DefaultHttpContext();
        context.Request.Headers[
            OperatorCredential.HeaderName] = Token;

        Assert.True(
            credential.Authorizes(
                context.Request.Headers));
        Assert.Equal(path, store.LastExplicitPath);
    }

    [Fact]
    public void ExplicitCredentialPath_ReportsReadFailure()
    {
        const string path =
            "/mohist-tests/operator/missing/token";
        var configuration = ConfigurationWith(
            "Mohist:OperatorTokenPath",
            path);

        var error = Assert.Throws<InvalidOperationException>(
            () => new OperatorCredential(
                configuration,
                EmptyEnvironment(),
                new InMemoryCredentialStore()));

        Assert.Contains(
            path,
            error.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration ConfigurationWith(
        string key,
        string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [key] = value,
                })
            .Build();

    private static MockEnvironmentVariableProvider
        EmptyEnvironment() =>
            new(addExistingEnvironmentVariables: false);

    private sealed class InMemoryCredentialStore
        : OperatorCredential.IOperatorCredentialStore
    {
        private readonly Dictionary<string, string>
            _tokens = new(StringComparer.Ordinal);

        public int CreateCount { get; private set; }

        public string? LastExplicitPath { get; private set; }

        public void Set(string path, string token) =>
            _tokens[path] = token;

        public string LoadOrCreateDefault(string path)
        {
            if (_tokens.TryGetValue(path, out var token))
                return token;

            CreateCount++;
            token =
                "generated-operator-token-0123456789abcdef";
            _tokens[path] = token;
            return token;
        }

        public string ReadExplicit(string path)
        {
            LastExplicitPath = path;
            if (_tokens.TryGetValue(path, out var token))
                return token;

            throw new InvalidOperationException(
                $"Mohist operator credential could not " +
                $"be read from '{path}'.");
        }
    }
}
