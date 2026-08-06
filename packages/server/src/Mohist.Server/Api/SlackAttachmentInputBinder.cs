using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

public sealed class SlackAttachmentInputBinder(
    AttachmentService attachments,
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

            results.Add(Rejected(
                attachmentId,
                AgentInputAttachmentRejectionReason.NotReadable,
                "Slack file content must be supplied by the adapter."));
        }
        return new SlackAttachmentBinding(results);
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
