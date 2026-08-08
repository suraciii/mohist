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

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

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

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

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

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

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

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

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

        Assert.Null(await provider.TryResolveAsync(new Uri("http://localhost:3456")));
    }

    [Fact]
    public async Task ShortTokens_AreRejected()
    {
        var files = new FakeFileSystem();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = "short";
        var provider = new CliCredentialProvider(files, env);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TryResolveAsync(new Uri("http://localhost:3456")));

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

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

        Assert.Equal(Token, credential!.Token);
    }

    [Fact]
    public async Task StoredSession_MatchesTheDestinationServer()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/credentials.json", """
            {"servers":[{"server":"http://localhost:3456","accessToken":"moh_session_0123456789abcdef0123456789abcdef","refreshToken":"moh_refresh_0123456789abcdef0123456789abcdef","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

        Assert.NotNull(credential);
        Assert.Equal("moh_session_0123456789abcdef0123456789abcdef", credential.Token);
        Assert.False(credential.MachineLocal);
        Assert.Equal(CliCredentialSource.CredentialFile, credential.Source);
        Assert.NotNull(credential.Stored);
    }

    [Fact]
    public async Task StoredSession_ForAnotherServer_DoesNotApply()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/credentials.json", """
            {"servers":[{"server":"http://localhost:3456","accessToken":"moh_session_0123456789abcdef0123456789abcdef","refreshToken":"moh_refresh_0123456789abcdef0123456789abcdef","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);
        files.AddFile("/home/test/.mohist/admin-token", "file-token-0123456789abcdef0123456789");
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        // No session for that server; resolution falls back to the
        // machine-local admin file, which is flagged machine-local so the
        // handler never attaches it to a remote destination.
        var credential = await provider.TryResolveAsync(new Uri("https://remote.example"));

        Assert.NotNull(credential);
        Assert.True(credential.MachineLocal);
        Assert.Equal(CliCredentialSource.AdminFile, credential.Source);
    }

    [Fact]
    public async Task MohistToken_TakesPrecedenceOverAStoredSession()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/credentials.json", """
            {"servers":[{"server":"http://localhost:3456","accessToken":"moh_session_0123456789abcdef0123456789abcdef","refreshToken":"moh_refresh_0123456789abcdef0123456789abcdef","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["MOHIST_TOKEN"] = Token;
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.Equal(CliCredentialSource.EnvironmentToken, credential.Source);
    }

    [Fact]
    public async Task StoredSession_TakesPrecedenceOverTheAdminFile()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.mohist/credentials.json", """
            {"servers":[{"server":"http://localhost:3456","accessToken":"moh_session_0123456789abcdef0123456789abcdef","refreshToken":"moh_refresh_0123456789abcdef0123456789abcdef","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);
        files.AddFile("/home/test/.mohist/admin-token", "file-token-0123456789abcdef");
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        env["HOME"] = "/home/test";
        var provider = new CliCredentialProvider(files, env);

        var credential = await provider.TryResolveAsync(new Uri("http://localhost:3456"));

        Assert.NotNull(credential);
        Assert.Equal("moh_session_0123456789abcdef0123456789abcdef", credential.Token);
        Assert.False(credential.MachineLocal);
    }
}
