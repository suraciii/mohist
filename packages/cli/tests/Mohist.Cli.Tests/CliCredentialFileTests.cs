using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliCredentialFileTests
{
    private const string Server = "http://localhost:3456";
    private const string Path = "/mohist-tests/user/.mohist/credentials.json";

    private static readonly StoredCliCredential Entry = new(
        Server,
        "moh_session_0123456789abcdef0123456789abcdef",
        "moh_refresh_0123456789abcdef0123456789abcdef",
        new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Save_ThenFind_RoundTrips_TheEntry()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);

        await file.SaveAsync(Entry);

        var found = await file.FindAsync(Server);
        Assert.NotNull(found);
        Assert.Equal(Entry.Server, found.Server);
        Assert.Equal(Entry.AccessToken, found.AccessToken);
        Assert.Equal(Entry.RefreshToken, found.RefreshToken);
        Assert.Equal(Entry.AccessExpiresAt, found.AccessExpiresAt);
        Assert.Equal(Entry.RefreshExpiresAt, found.RefreshExpiresAt);
    }

    [Fact]
    public async Task Find_MatchesServersIgnoringTrailingSlash_AndOtherServers()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);
        await file.SaveAsync(Entry);

        Assert.NotNull(await file.FindAsync($"{Server}/"));
        Assert.Null(await file.FindAsync("https://elsewhere.example"));
    }

    [Fact]
    public async Task Save_UpsertsTheEntryForTheServer()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);
        await file.SaveAsync(Entry);
        var replacement = Entry with { AccessToken = "moh_session_new" };

        await file.SaveAsync(replacement);

        var document = JsonNode.Parse(fs.ReadAllText(Path))!;
        var servers = document["servers"]!.AsArray();
        Assert.Single(servers);
        Assert.Equal("moh_session_new", servers[0]!["accessToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheMatchingServer()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);
        await file.SaveAsync(Entry);
        await file.SaveAsync(Entry with { Server = "https://elsewhere.example" });

        await file.RemoveAsync(Server);

        var remaining = await file.FindAsync("https://elsewhere.example");
        Assert.NotNull(remaining);
        Assert.Null(await file.FindAsync(Server));
    }

    [Fact]
    public async Task Find_OnAMissingOrCorruptFile_ReturnsNull()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);

        Assert.Null(await file.FindAsync(Server));

        fs.AddFile(Path, "{not json");
        Assert.Null(await file.FindAsync(Server));
    }

    [Fact]
    public async Task Save_AfterACorruptFile_ReplacesIt()
    {
        var fs = new FakeFileSystem();
        var file = new CliCredentialFile(fs, Path);
        fs.AddFile(Path, "{not json");

        await file.SaveAsync(Entry);

        Assert.NotNull(await file.FindAsync(Server));
    }

    [Fact]
    public void PathFor_ResolvesUnderTheMohistDirectory()
    {
        Assert.Equal(
            "/home/test/.mohist/credentials.json",
            CliCredentialFile.PathFor(() => "/home/test"));
    }
}
