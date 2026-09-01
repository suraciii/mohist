using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.L1Tests.Support;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

public sealed class SlackManagerReplyRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerReplyRouteSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_reply_validates_full_anchor_promotes_progress_and_deduplicates_retries()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var enrollmentId = $"manager-reply-route-{suffix}";
        var workspace = $"T_MANAGER_REPLY_{suffix}";
        var conversation = $"D_MANAGER_REPLY_{suffix}";
        var triggeringMessage = "1710000000.000001";
        var sessionId = $"manager-session-{suffix}";
        var source = new SlackMessageIdentity(workspace, conversation, triggeringMessage);
        var origin = new ManagerExecutionOrigin(
            workspace,
            conversation,
            triggeringMessage,
            triggeringMessage,
            "U_MANAGER_REPLY",
            enrollmentId,
            sessionId,
            $"slack:{sessionId}:input-1");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var now = _fixture.TimeProvider.GetUtcNow();
            db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
            {
                Id = enrollmentId,
                WorkspaceTeamId = workspace,
                Lifecycle = SlackEnrollmentLifecycle.Active,
                ManagerCapability = SlackManagerCapability.Available,
                ManagerReadiness = SlackManagerReadiness.Ready,
                ManagerAppId = $"A_MANAGER_{suffix}",
                ManagerBotUserId = $"U_MANAGER_BOT_{suffix}",
                ManagerActorId = "manager-actor",
                ClaimedSlackUserId = "U_MANAGER_REPLY",
                ManagerCredentialRef = $"manager-credential-{suffix}",
                PlanCode = "unknown",
                AuditJson = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.SlackProviderInboxRows.Add(new SlackProviderInboxRow
            {
                Id = $"slkinb_{suffix}",
                ProjectId = SlackDeliveryOwnerIds.ManagerProjectId,
                ConnectionId = enrollmentId,
                SlackMessageIdentity = source.AsKey(),
                WorkspaceTeamId = workspace,
                ConversationId = conversation,
                ThreadTs = null,
                SlackUserId = "U_MANAGER_REPLY",
                RouteKind = SlackProviderInboxRouteKinds.Launch,
                RouteSessionId = sessionId,
                AcceptedAt = now,
                DispatchedAt = now,
                CreatedAt = now,
            });
            db.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
            {
                Id = $"slkdmmp_{suffix}",
                ProjectId = SlackDeliveryOwnerIds.ManagerProjectId,
                ConnectionId = enrollmentId,
                WorkspaceTeamId = workspace,
                SlackUserId = "U_MANAGER_REPLY",
                DmConversationId = conversation,
                CurrentSessionId = sessionId,
                CurrentMessageTs = triggeringMessage,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();

            var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
            await projection.EnqueueWorkingAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollmentId,
                source,
                null,
                SlackStatusProjection.DispatchRef(source, "progress"));
        }

        ManagerExecutionGrant grant;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var issuer = scope.ServiceProvider.GetRequiredService<ManagerExecutionCapabilityIssuer>();
            grant = issuer.Issue(new ManagerExecutionIssueRequest(
                $"manager:job-{suffix}:work-1:0",
                origin,
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMinutes(5)));
        }

        using (var mismatch = BuildRequest(grant.ReplyCredential, new
        {
            conversationId = conversation,
            threadTs = triggeringMessage,
            triggeringMessageId = "1710000000.000099",
            text = "should be rejected",
        }))
        using (var mismatchResponse = await _fixture.Client.SendAsync(mismatch))
        {
            Assert.Equal(HttpStatusCode.Conflict, mismatchResponse.StatusCode);
        }

        using var first = BuildRequest(grant.ReplyCredential, new
        {
            conversationId = conversation,
            threadTs = triggeringMessage,
            triggeringMessageId = triggeringMessage,
            text = "the authoritative answer",
        });
        using var firstResponse = await _fixture.Client.SendAsync(first);
        firstResponse.EnsureSuccessStatusCode();

        using var duplicate = BuildRequest(grant.ReplyCredential, new
        {
            conversationId = conversation,
            threadTs = triggeringMessage,
            text = "a duplicate answer must not append",
        });
        using var duplicateResponse = await _fixture.Client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        using var identical = BuildRequest(grant.ReplyCredential, new
        {
            conversationId = conversation,
            threadTs = triggeringMessage,
            text = "the authoritative answer",
        });
        using var identicalResponse = await _fixture.Client.SendAsync(identical);
        identicalResponse.EnsureSuccessStatusCode();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var outbox = verify.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var projectionAfterReply = verify.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        await projectionAfterReply.FinalizeLivenessAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            enrollmentId,
            source,
            null,
            "completed");
        await projectionAfterReply.FinalizeLivenessAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            enrollmentId,
            source,
            null,
            "completed");

        var rows = (await outbox.ListManagerAsync(enrollmentId)).Entries;
        var reply = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.TerminalResult);
        Assert.Equal(SlackDeliveryOwnerIds.ManagerProjectId, reply.ProjectId);
        Assert.Equal(SlackDeliveryOwnerKinds.Manager, reply.OwnerKind);
        Assert.Equal(enrollmentId, reply.ConnectionId);
        Assert.Equal("the authoritative answer", SlackDeliveryPayload.Parse(reply.PayloadJson).Text);
        Assert.DoesNotContain("duplicate answer", reply.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, row => row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var terminalReaction = Assert.Single(
            rows,
            row => row.DispatchRef == SlackStatusProjection.DispatchRef(source, "terminal-add"));
        Assert.Equal("white_check_mark", SlackDeliveryPayload.Parse(terminalReaction.PayloadJson).Reaction);
    }

    private HttpRequestMessage BuildRequest(string credential, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slack-manager/reply")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.TryAddWithoutValidation("X-Mohist-Manager-Mode", "1");
        return request;
    }
}
