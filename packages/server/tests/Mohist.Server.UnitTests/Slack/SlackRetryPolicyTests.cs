using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackRetryPolicyTests
{
    [Theory]
    [InlineData("runner-unavailable")]
    [InlineData(" runner-lost ")]
    [InlineData("REPORT-TIMEOUT")]
    [InlineData("timeout")]
    [InlineData("deadline-exceeded")]
    [InlineData("probe_timeout")]
    [InlineData("opencode-transport-failed")]
    [InlineData("unavailable-runtime")]
    [InlineData("rate_limited")]
    [InlineData("retry-safe")]
    public void Allowlist_accepts_only_the_reviewed_categories(string category)
    {
        Assert.True(SlackTurnControlService.IsRetryableFailureCategory(category));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("turn-failed")]
    [InlineData("invalid-input")]
    [InlineData("permission-required")]
    [InlineData("configuration")]
    [InlineData("context_exhausted")]
    [InlineData("runtime-session-missing")]
    [InlineData("runner unavailable")]
    public void Unknown_or_unreviewed_categories_are_text_only(string? category)
    {
        Assert.False(SlackTurnControlService.IsRetryableFailureCategory(category));
    }
}
