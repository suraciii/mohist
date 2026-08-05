using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackOutboundPortTests
{
    [Fact]
    public void Server_removes_the_legacy_wide_Slack_api_client()
    {
        var serverAssembly = typeof(ISlackAppManagementPort).Assembly;

        Assert.Null(serverAssembly.GetType("Mohist.Server.Slack.ISlackApiClient"));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackConfigurationCredentialPort).FullName!));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackAppManagementPort).FullName!));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackBotIdentityVerificationPort).FullName!));
    }

    [Fact]
    public async Task ConfigurationCredentialPort_ReceivesOnlyTheCredentialPair()
    {
        var port = new FakeSlackConfigurationCredentialPort();
        port.Enqueue(new(SlackConfigurationCredentialRotationOutcome.Succeeded, new("next-access", "next-refresh"), "T123", DateTimeOffset.UnixEpoch.AddHours(1)));

        var result = await port.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Succeeded, result.Outcome);
        Assert.Equal([new SlackConfigurationCredentialPair("access", "refresh")], port.Requests);
    }

    [Fact]
    public async Task AppManagementPort_UsesManifestRequestsForValidationUpdateAndExport()
    {
        var port = new FakeSlackAppManagementPort();
        var app = new SlackAppManagementRequest("enrollment", "child", "T123", "A123");
        var manifest = new SlackManifest(2, "{}", "hash");
        port.SetResponse("child", new(Export: new(SlackAppManagementFactOutcome.Present, "{}")));

        await port.ValidateManifestAsync(new(app, manifest));
        await port.UpdateManifestAsync(new(app, manifest));
        var exported = await port.ExportManifestAsync(app);

        Assert.Equal(2, port.ManifestCalls);
        Assert.Equal(SlackAppManagementFactOutcome.Present, exported.Outcome);
    }

    [Fact]
    public async Task BotIdentityVerificationPort_ReceivesOnlyCandidateBotToken()
    {
        var port = new FakeSlackBotIdentityVerificationPort
        {
            Result = new(true, "T123", "U123", "A123", new HashSet<string>(["chat:write"]))
        };

        var result = await port.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.Equal([new SlackBotIdentityVerificationRequest("xoxb-candidate")], port.Requests);
    }
}
