using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Slack;

public sealed class SlackSelectionActionPayloadTests
{
    [Fact]
    public async Task Two_to_five_candidates_render_one_signed_button_each_with_one_expiry()
    {
        var signer = new RecordingSigner();
        var connection = new AgentConnection
        {
            ProjectId = "project-owner",
            Id = "connection-owner",
            WorkspaceTeamId = "team-1",
            BotUserId = "UOWNER",
        };
        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-owner", "connection-owner"),
            new SlackSelectionCandidateReference("project-other", "connection-other"),
        };
        var expiresAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        var blocks = await SlackSelectionChooserRenderer.BuildBlocksAsync(
            signer,
            connection,
            "team-1",
            "channel-1",
            "100.001",
            null,
            "UACTOR",
            SlackAmbiguityKinds.RootMultiMention,
            candidates,
            ["Bot A", "Bot B"],
            expiresAt,
            CancellationToken.None);

        Assert.True(blocks.HasValue);
        var actions = blocks!.Value[0].GetProperty("elements");
        Assert.Equal(2, actions.GetArrayLength());
        Assert.Equal(2, signer.Canonicals.Count);

        var first = JSON.Deserialize<SlackSelectionActionPayload>(
            actions[0].GetProperty("value").GetString()!);
        Assert.NotNull(first);
        Assert.True(SlackSelectionActionPayload.IsStructurallyValid(first));
        Assert.Equal("project-owner", first!.ProjectId);
        Assert.Equal("connection-owner", first.ChosenConnectionId);
        Assert.Equal(expiresAt, first.ExpiresAt);
        Assert.Equal(SlackSelectionActionPayload.ActionId, actions[0].GetProperty("action_id").GetString());
    }

    [Fact]
    public async Task More_than_five_candidates_render_readable_text_without_controls()
    {
        var candidates = Enumerable.Range(0, 6)
            .Select(index => new SlackSelectionCandidateReference(
                $"project-{index}", $"connection-{index}"))
            .ToArray();
        var blocks = await SlackSelectionChooserRenderer.BuildBlocksAsync(
            new RecordingSigner(),
            new AgentConnection
            {
                ProjectId = "project-owner",
                Id = "connection-owner",
                WorkspaceTeamId = "team-1",
                BotUserId = "UOWNER",
            },
            "team-1",
            "channel-1",
            "100.001",
            null,
            "UACTOR",
            SlackAmbiguityKinds.RootMultiMention,
            candidates,
            candidates.Select(candidate => candidate.ConnectionId).ToArray(),
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.False(blocks.HasValue);
    }

    [Fact]
    public void Candidate_order_is_part_of_the_canonical_signed_form()
    {
        var payload = new SlackSelectionActionPayload(
            "v1",
            SlackSelectionActionPayload.ActionName,
            "project-owner",
            "connection-owner",
            "team-1",
            "channel-1",
            "100.001",
            null,
            SlackAmbiguityKinds.RootMultiMention,
            "UACTOR",
            [
                new("project-a", "connection-a"),
                new("project-b", "connection-b"),
            ],
            "project-a",
            "connection-a",
            "nonce",
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            null);
        var reordered = payload with
        {
            CandidateReferences = payload.CandidateReferences.Reverse().ToArray(),
        };

        Assert.NotEqual(
            SlackSelectionActionPayload.Canonical(payload),
            SlackSelectionActionPayload.Canonical(reordered));
    }

    private sealed class RecordingSigner : ISlackActionSigner
    {
        public List<string> Canonicals { get; } = [];

        public Task<string?> TrySignAsync(
            AgentConnection connection,
            string canonical,
            CancellationToken ct = default)
        {
            Canonicals.Add(canonical);
            return Task.FromResult<string?>("signed");
        }

        public Task<bool> VerifyAsync(
            AgentConnection connection,
            string canonical,
            string? signature,
            CancellationToken ct = default) =>
            Task.FromResult(signature == "signed");
    }
}
