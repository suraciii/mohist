using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Slack;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

public sealed class SlackAttachmentInputBinder(
    AttachmentService attachments,
    ISlackApiClient slack,
    ISecretStore secrets,
    IOptions<AttachmentStorageOptions> storageOptions)
{
    public async Task<SlackAttachmentBinding> PrepareAsync(
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        string agentSessionId,
        string inputId,
        IReadOnlyList<SlackIngressFile>? files,
        CancellationToken ct = default)
    {
        if (files is null || files.Count == 0)
            return new SlackAttachmentBinding([]);

        var results = new List<AgentInputAttachmentAcceptance>(files.Count);
        var candidates = new List<string>();
        var boundIds = new List<string>();
        byte[]? botToken = null;
        var tokenLoaded = false;

        foreach (var file in files)
        {
            var attachmentId = DeterministicAttachmentId(identity, file.Id);
            if (file.Size > storageOptions.Value.MaxFileBytes)
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.ExceedsSizeLimit,
                    $"Attachment exceeds the configured size limit of {storageOptions.Value.MaxFileBytes} bytes."));
                continue;
            }

            if (!AttachmentService.IsAcceptableAgentInputContentType(file.Mimetype))
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.UnsupportedType,
                    $"Attachment content-type '{file.Mimetype}' is not supported."));
                continue;
            }

            if (await attachments.ExistsAsync(projectId, attachmentId, ct).ConfigureAwait(false))
            {
                candidates.Add(attachmentId);
                results.Add(new AgentInputAttachmentAcceptance(attachmentId, null, null, null));
                continue;
            }

            if (!tokenLoaded)
            {
                botToken = await secrets.LoadAsync(
                    new SecretStoreAddress(projectId, connection.Id, SecretKind.BotToken), ct).ConfigureAwait(false);
                tokenLoaded = true;
            }

            if (botToken is null || botToken.Length == 0)
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.NotReadable,
                    "Slack file content could not be read by the Connection Bot."));
                continue;
            }

            try
            {
                using var content = await slack.OpenFileContentAsync(
                    file.Id,
                    System.Text.Encoding.UTF8.GetString(botToken),
                    ct).ConfigureAwait(false);
                await attachments.IngestProviderFileAsync(
                    projectId,
                    attachmentId,
                    source: "slack",
                    content.FileName,
                    content.ContentType,
                    content.Size,
                    content.Stream,
                    ct).ConfigureAwait(false);
                candidates.Add(attachmentId);
                results.Add(new AgentInputAttachmentAcceptance(attachmentId, null, null, null));
            }
            catch (SlackFileNotReadableException)
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.NotReadable,
                    "Slack file content could not be read by the Connection Bot."));
            }
            catch (AttachmentLimitException ex)
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.ExceedsSizeLimit,
                    ex.Message));
            }
            catch (HttpRequestException)
            {
                results.Add(Rejected(
                    attachmentId,
                    AgentInputAttachmentRejectionReason.NotReadable,
                    "Slack file content could not be read by the Connection Bot."));
            }
        }

        if (candidates.Count > 0)
        {
            var bound = await attachments.ValidateAndBindAgentInputAsync(
                projectId,
                agentSessionId,
                inputId,
                candidates,
                ct).ConfigureAwait(false);
            var verdictById = bound.Results.ToDictionary(result => result.Id, StringComparer.Ordinal);
            for (var index = 0; index < results.Count; index++)
            {
                var existing = results[index];
                if (verdictById.TryGetValue(existing.Id, out var verdict))
                    results[index] = verdict;
            }
            foreach (var newlyBound in bound.NewlyBoundAttachmentIds ?? Array.Empty<string>())
                boundIds.Add(newlyBound);
        }

        return new SlackAttachmentBinding(
            results,
            boundIds);
    }

    public Task RollbackAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        SlackAttachmentBinding binding,
        CancellationToken ct = default) =>
        attachments.UnbindAgentInputAsync(
            projectId,
            agentSessionId,
            inputId,
            binding.NewlyBoundAttachmentIds ?? [],
            ct);

    public static string DeterministicAttachmentId(SlackMessageIdentity identity, string slackFileId) =>
        $"att_{AgentLaunchCoordinatorCodec.StableToken($"{identity.WorkspaceTeamId}/{identity.ConversationId}/{identity.MessageTs}/{slackFileId}")}";

    private static AgentInputAttachmentAcceptance Rejected(
        string id,
        AgentInputAttachmentRejectionReason reason,
        string message) =>
        new(id, null, reason, message);
}

public sealed record SlackAttachmentBinding(
    IReadOnlyList<AgentInputAttachmentAcceptance> Results,
    IReadOnlyList<string>? NewlyBoundAttachmentIds = null)
{
    public IReadOnlyList<string> AttachmentIds =>
        Results.Select(result => result.Id).Distinct(StringComparer.Ordinal).ToArray();

    public int AcceptedCount => Results.Count(result => result.IsAccepted);

    public IReadOnlyList<AgentSessionInputAttachmentDescriptor> AcceptedDescriptors =>
        Results.Where(result => result.IsAccepted && result.Descriptor is not null)
            .Select(result => result.Descriptor!)
            .ToArray();
}

public sealed record SlackIngressFile(
    string Id,
    string Name,
    string Mimetype,
    long Size);
