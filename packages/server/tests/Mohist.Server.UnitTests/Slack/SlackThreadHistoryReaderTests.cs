using System.Text;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackThreadHistoryReaderTests
{
    [Fact]
    public async Task ReadAsync_NoPriorMessages_ReturnsEmpty()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesResult = new SlackConversationsRepliesPage(true, null, [], null);
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Empty, result.Outcome);
        Assert.Empty(result.Messages);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ReadAsync_PriorMessages_ReturnedInReadOrder()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesResult = new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710.000010", "U1", "hello"), Message("1710.000020", "U2", "world") },
            null);
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Imported, result.Outcome);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("1710.000010", result.Messages[0].Ts);
        Assert.Equal("1710.000020", result.Messages[1].Ts);
    }

    [Fact]
    public async Task ReadAsync_KeepsPriorMessages_StopsAtMention()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesResult = new SlackConversationsRepliesPage(
            true,
            null,
            new[]
            {
                Message("1710.000010", "U1", "older"),
                Message("1710.000100", "U_OWNER", "<@U_BOT> task"),
            },
            null);
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Imported, result.Outcome);
        Assert.Single(result.Messages);
        Assert.Equal("1710.000010", result.Messages[0].Ts);
    }

    [Fact]
    public async Task ReadAsync_MentionExcluded_PostMentionMessagesDropped()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesResult = new SlackConversationsRepliesPage(
            true,
            null,
            new[]
            {
                Message("1710.000010", "U1", "older"),
                Message("1710.000100", "U_OWNER", "<@U_BOT> task"),
                Message("1710.000200", "U2", "after the mention"),
            },
            null);
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Imported, result.Outcome);
        Assert.Single(result.Messages);
        Assert.Equal("1710.000010", result.Messages[0].Ts);
    }

    [Fact]
    public async Task ReadAsync_SlackError_ReturnsRefused()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesError = new SlackConversationsRepliesPage(false, "not_in_channel", null, null);
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Refused, result.Outcome);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("not_in_channel", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_TransportFailure_ReturnsRefused()
    {
        var fake = new FakeSlackApiClient { ThrowOnReplies = new HttpRequestException("slack down") };
        var reader = NewReader(fake, NewSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Refused, result.Outcome);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task ReadAsync_PaginationHonorsNextCursor_AndDepthCap()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesPages.Enqueue(new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710.000010", "U1", "first page") },
            new SlackResponseMetadata("cursor-2")));
        fake.ConversationsRepliesPages.Enqueue(new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710.000020", "U2", "second page") },
            null));
        var reader = NewReader(fake, NewSecretStore(), depthCap: 5);

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Imported, result.Outcome);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(2, fake.RepliesCalls);
    }

    [Fact]
    public async Task ReadAsync_PaginationDepthCap_RefusesWhenMentionBeyondCap()
    {
        var fake = new FakeSlackApiClient();
        for (var i = 0; i < 5; i++)
            fake.ConversationsRepliesPages.Enqueue(new SlackConversationsRepliesPage(
                true,
                null,
                new[] { Message($"1710.0000{i}0", "U1", $"page-{i}") },
                new SlackResponseMetadata($"cursor-{i + 1}")));
        var reader = NewReader(fake, NewSecretStore(), depthCap: 3);

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Refused, result.Outcome);
        Assert.Equal(3, fake.RepliesCalls);
        Assert.Contains("depth cap", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_PaginationCompletes_ImportsWhenThreadEndsBeforeMention()
    {
        var fake = new FakeSlackApiClient();
        fake.ConversationsRepliesPages.Enqueue(new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710.000010", "U1", "only message") },
            null));
        var reader = NewReader(fake, NewSecretStore(), depthCap: 2);

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Imported, result.Outcome);
        Assert.Single(result.Messages);
    }

    [Fact]
    public async Task ReadAsync_NoBotToken_ReturnsRefused()
    {
        var reader = NewReader(new FakeSlackApiClient(), new FakeSecretStore());

        var result = await reader.ReadAsync("proj", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Refused, result.Outcome);
        Assert.Contains("bot token", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyBudget_UnderBudget_NoTruncation()
    {
        var messages = new[]
        {
            Message("1710.000010", "U1", "hi"),
            Message("1710.000020", "U2", "there"),
        };

        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(messages, 1024);

        Assert.Null(marker);
        Assert.Equal(0, omitted);
        Assert.Contains("U1: hi", text, StringComparison.Ordinal);
        Assert.Contains("U2: there", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyBudget_OverBudget_DropsOldestFirst_AndMarksTruncation()
    {
        var longText = new string('a', 100);
        var messages = new[]
        {
            Message("1710.000010", "U1", longText),
            Message("1710.000020", "U2", longText),
            Message("1710.000030", "U3", longText),
        };

        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(messages, 250);

        Assert.NotNull(marker);
        Assert.Equal(1, omitted);
        Assert.Contains("1 oldest messages omitted", marker!, StringComparison.Ordinal);
        Assert.Contains("U2: ", text, StringComparison.Ordinal);
        Assert.Contains("U3: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("U1: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyBudget_OutOfOrderMessages_ResortedByTimestamp()
    {
        var longText = new string('a', 100);
        var messages = new[]
        {
            Message("1710.000030", "U3", longText),
            Message("1710.000010", "U1", longText),
            Message("1710.000020", "U2", longText),
        };

        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(messages, 250);

        Assert.NotNull(marker);
        Assert.Equal(1, omitted);
        Assert.DoesNotContain("U1: ", text, StringComparison.Ordinal);
    }

    private static SlackConversationMessage Message(string ts, string user, string text) =>
        new(
            Type: "message",
            Subtype: null,
            Ts: ts,
            User: user,
            Text: text,
            BotId: null,
            ThreadTs: null,
            ParentUserId: null);

    private static SlackThreadHistoryReader NewReader(
        ISlackApiClient slack,
        ISecretStore secrets,
        int depthCap = 5,
        int budget = 8000)
    {
        var options = Options.Create(new SlackProviderOptions
        {
            StartupContextCharacterBudget = budget,
            StartupContextPaginationDepthCap = depthCap,
        });
        return new SlackThreadHistoryReader(slack, secrets, options);
    }

    private static FakeSecretStore NewSecretStore(string token = "xoxb-test")
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var store = new FakeSecretStore();
        store.Set(new SecretStoreAddress("proj", "connection", SecretKind.BotToken), bytes);
        return store;
    }

    private sealed class FakeSlackApiClient : ISlackApiClient
    {
        public SlackConversationsRepliesPage ConversationsRepliesResult { get; set; } = new(true, null, [], null);
        public SlackConversationsRepliesPage? ConversationsRepliesError { get; set; }
        public Queue<SlackConversationsRepliesPage> ConversationsRepliesPages { get; } = new();
        public Exception? ThrowOnReplies { get; set; }
        public int RepliesCalls { get; private set; }

        public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
            string conversationId,
            string threadTs,
            string? cursor,
            string botToken,
            CancellationToken ct = default)
        {
            RepliesCalls++;
            if (ThrowOnReplies is not null)
                throw ThrowOnReplies;
            if (ConversationsRepliesError is not null)
                return Task.FromResult(ConversationsRepliesError);
            if (ConversationsRepliesPages.Count > 0)
                return Task.FromResult(ConversationsRepliesPages.Dequeue());
            return Task.FromResult(ConversationsRepliesResult);
        }

        public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = new();

        public void Set(SecretStoreAddress address, byte[] value) => _values[address] = value;

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _values[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(address, out var value) ? value : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
