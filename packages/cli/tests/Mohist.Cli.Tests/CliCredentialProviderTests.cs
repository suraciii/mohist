using Mohist.Cli.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Cli.Tests;

public sealed class CliCredentialProviderTests
{
    private const string Token = "test-admin-token-0123456789abcdef";

    [Fact]
    public async Task EnvironmentVariable_TakesPrecedenceOverTheDefaultFile()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = Token;
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        Assert.Equal(Token, await provider.GetAsync());
    }

    [Fact]
    public async Task MissingEnvironmentVariable_ReadsTheDefaultAdminTokenFile()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/admin-token", Token);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        Assert.Equal(Token, await provider.GetAsync());
    }

    [Fact]
    public async Task MissingCredential_FailsWithAnActionableMessage()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync());

        Assert.Contains("/home/test/.mohist/admin-token", error.Message, StringComparison.Ordinal);
        Assert.Contains("MOHIST_TOKEN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortTokens_AreRejected()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = "short";
        var provider = new CliCredentialProvider(files, env);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync());

        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhitespaceAroundFileToken_IsTrimmed()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/admin-token", $"  {Token}\n");
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        Assert.Equal(Token, await provider.GetAsync());
    }
}
