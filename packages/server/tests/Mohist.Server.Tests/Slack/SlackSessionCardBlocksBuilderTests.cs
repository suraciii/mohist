using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackSessionCardBlocksBuilderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_AlwaysShowsIdentityBeforeOptionalNavigationAndUnchangedControls(
        bool hasLink,
        bool hasControls)
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using (var db = database.CreateContext())
        {
            db.Projects.Add(new ProjectRow
            {
                Id = "project-1",
                Name = "demo",
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }
        var builder = Build(database, hasLink ? "https://mohist.example/base" : null);
        JsonElement? controls = hasControls ? StopBlocks() : null;

        var blocks = await builder.BuildAsync("project-1", "canonical-session-1", controls);

        Assert.Equal(1 + (hasLink ? 1 : 0) + (hasControls ? 1 : 0), blocks.GetArrayLength());
        AssertIdentity(blocks[0], "canonical-session-1");
        if (hasLink)
        {
            var navigation = blocks[1];
            Assert.Equal("section", navigation.GetProperty("type").GetString());
            Assert.Equal("mrkdwn", navigation.GetProperty("text").GetProperty("type").GetString());
            Assert.Equal(
                "<https://mohist.example/base/demo/sessions/canonical-session-1|Open in Mohist>",
                navigation.GetProperty("text").GetProperty("text").GetString());
            Assert.False(navigation.TryGetProperty("elements", out _));
            Assert.False(navigation.TryGetProperty("action_id", out _));
            Assert.False(navigation.TryGetProperty("value", out _));
        }
        if (hasControls)
            Assert.Equal(controls!.Value[0].GetRawText(), blocks[blocks.GetArrayLength() - 1].GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_UnresolvedProjectKeepsIdentityAndControlsWithoutNavigation(bool hasControls)
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var builder = Build(database, "https://mohist.example");
        JsonElement? controls = hasControls ? StopBlocks() : null;

        var blocks = await builder.BuildAsync("missing-project", "canonical-session-1", controls);

        Assert.Equal(hasControls ? 2 : 1, blocks.GetArrayLength());
        AssertIdentity(blocks[0], "canonical-session-1");
        if (hasControls)
            Assert.Equal(controls!.Value[0].GetRawText(), blocks[1].GetRawText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://localhost:5173")]
    public async Task BuildAsync_UnusableOriginKeepsLiteralIdentityWithoutReadingTheProject(string? origin)
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        var builder = Build(database, origin);

        var blocks = await builder.BuildAsync("project-1", "session-<@member>|&", null);

        AssertIdentity(Assert.Single(blocks.EnumerateArray()), "session-<@member>|&");
    }

    [Theory]
    [InlineData(null, "session-1")]
    [InlineData("", "session-1")]
    [InlineData(" ", "session-1")]
    [InlineData("project-1", null)]
    [InlineData("project-1", "")]
    [InlineData("project-1", " ")]
    public async Task BuildAsync_RejectsMissingIdentityBeforeReadingTheProject(string? projectId, string? sessionId)
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        var builder = Build(database, "https://mohist.example");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => builder.BuildAsync(projectId!, sessionId!, null));
    }

    private static SlackSessionCardBlocksBuilder Build(TestSqliteDatabase database, string? origin) =>
        new(
            new SlackWebLinkBuilder(Options.Create(new SlackProviderOptions { ExternalWebUrl = origin })),
            new ProjectQuerier(new TestDbContextFactory(database.Options)));

    private static void AssertIdentity(JsonElement section, string sessionId)
    {
        Assert.Equal("section", section.GetProperty("type").GetString());
        Assert.Equal("plain_text", section.GetProperty("text").GetProperty("type").GetString());
        Assert.Equal($"Session: {sessionId}", section.GetProperty("text").GetProperty("text").GetString());
    }

    private static JsonElement StopBlocks() => JsonSerializer.SerializeToElement(new[]
    {
        new
        {
            type = "actions",
            block_id = "mohist-turn-control",
            elements = new[]
            {
                new
                {
                    type = "button",
                    text = new { type = "plain_text", text = "Stop" },
                    style = "danger",
                    action_id = "mohist_stop_turn",
                    value = "signed-control-payload",
                },
            },
        },
    });
}
