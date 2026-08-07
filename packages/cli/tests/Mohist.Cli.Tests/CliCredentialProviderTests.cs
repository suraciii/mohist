using Mohist.Cli.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Cli.Tests;

public sealed class CliCredentialProviderTests
{
    private const string Token = "test-admin-token-0123456789abcdef";

    [Fact]
    public async Task MohistToken_TakesPrecedenceOverTheAdminCredential()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/admin-token", "file-token-0123456789abcdef");
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = Token;
        env["MOHIST_ADMIN_TOKEN"] = "admin-env-token-0123456789abcdef";
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync();

        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.False(credential.MachineLocal);
    }

    [Fact]
    public async Task MohistAdminToken_Value_TakesPrecedenceOverTheFile()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/admin-token", "file-token-0123456789abcdef");
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_ADMIN_TOKEN"] = Token;
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync();

        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.True(credential.MachineLocal);
    }

    [Fact]
    public async Task MohistAdminTokenPath_ReadsTheConfiguredFile()
    {
        var files = new FakeFileSystem();
        files.AddFile("/run/mohist/admin-token", Token);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_ADMIN_TOKEN_PATH"] = "/run/mohist/admin-token";
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync();

        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.True(credential.MachineLocal);
    }

    [Fact]
    public async Task MissingEnvironmentVariable_ReadsTheDefaultAdminTokenFile()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/admin-token", Token);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync();

        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.True(credential.MachineLocal);
    }

    [Fact]
    public async Task MissingCredential_ResolvesToNull()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        Assert.Null(await provider.TryResolveAsync());
    }

    [Fact]
    public async Task ShortTokens_AreRejected()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = "short";
        var provider = new CliCredentialProvider(files, env);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TryResolveAsync());

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

        var credential = await provider.TryResolveAsync();

        Assert.Equal(Token, credential!.Token);
    }
}
