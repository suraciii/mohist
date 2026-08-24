using System.CommandLine;

namespace Mohist.Cli;

internal static partial class SlackCommands
{
    private static Command BuildMessage(MohistCliApi api)
    {
        var message = new Command("message", "Send or read Slack messages on behalf of an Agent");
        message.Subcommands.Add(BuildMessageSend(api));
        return message;
    }

    private static Command BuildMessageSend(MohistCliApi api)
    {
        var command = new Command("send", "Send a reply to a Slack conversation (the Agent-authored reply body); --text - reads the body from stdin");
        var conversation = new Option<string>("--conversation")
        {
            Description = "Slack conversation id (channel or DM), read from the injected Slack reply anchor.",
            Required = true,
        };
        var replyTo = new Option<string?>("--reply-to")
        {
            Description = "Thread root message timestamp to reply in a thread (the reply anchor threadRootMessageId). Omit for a DM.",
        };
        var dispatchRef = new Option<string?>("--dispatch-ref")
        {
            Description = "Logical reply identity from the injected Slack reply anchor. Agent retries reuse it; separate turns use different values.",
        };
        var connection = new Option<string?>("--connection")
        {
            Description = "Owning Connection id from the injected Slack reply anchor. Required by the Server when --dispatch-ref is supplied.",
        };
        var triggeringMessage = new Option<string?>("--triggering-message")
        {
            Description = "Triggering Slack message id from the injected reply anchor. Required by the Server when --dispatch-ref is supplied.",
        };
        var text = new Option<string?>("--text")
        {
            Description = "Reply body in markdown (bold/code/lists/quotes render in Slack). Pass '-' to read it from standard input (preserves newlines).",
        };
        var file = new Option<string?>("--file")
        {
            Description = "Local image file to upload to Slack (base64-encoded to the Server; at most 10 MB).",
        };
        var image = new Option<string?>("--image")
        {
            Description = "Public image URL to display inline in the reply.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Options.Add(conversation);
        command.Options.Add(replyTo);
        command.Options.Add(dispatchRef);
        command.Options.Add(connection);
        command.Options.Add(triggeringMessage);
        command.Options.Add(text);
        command.Options.Add(file);
        command.Options.Add(image);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            string? projectId = null;
            if (!ManagerCliMode.Active)
            {
                var resolved = await ProjectAsync(api, ctx.GetValue(project));
                if (resolved.Exit != 0 || resolved.ProjectId is null) return resolved.Exit;
                projectId = resolved.ProjectId;
            }

            var body = ctx.GetValue(text);
            if (string.Equals(body, "-", StringComparison.Ordinal))
            {
                body = await api.StandardInput
                    .ReadToEndAsync(api.Invocation.CancellationToken)
                    .ConfigureAwait(false);
            }

            var filePath = ctx.GetValue(file);
            var imageUrl = ctx.GetValue(image);
            if (filePath is not null && imageUrl is not null)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--file and --image are mutually exclusive.");
            if (string.IsNullOrWhiteSpace(body) && filePath is null && imageUrl is null)
            {
                api.Error.WriteLine("--text, --file, or --image is required; nothing to send (silence is achieved by not running this command).");
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }
            if (imageUrl is not null
                && !imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--image must be a public http(s) image URL.");

            string? fileName = null;
            string? fileContentBase64 = null;
            if (filePath is not null)
            {
                if (!api.FileSystem.Exists(filePath) || api.FileSystem.DirectoryExists(filePath))
                {
                    api.Error.WriteLine($"--file '{filePath}' does not exist or is not a regular file.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                await using var stream = api.FileSystem.OpenRead(filePath);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, api.Invocation.CancellationToken).ConfigureAwait(false);
                if (buffer.Length == 0)
                {
                    api.Error.WriteLine($"--file '{filePath}' is empty.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }
                if (buffer.Length > 10 * 1024 * 1024)
                {
                    api.Error.WriteLine($"--file '{filePath}' exceeds the 10 MB upload limit.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                fileName = System.IO.Path.GetFileName(filePath);
                fileContentBase64 = Convert.ToBase64String(buffer.ToArray());
            }

            var replyPath = ManagerCliMode.Active
                ? "/api/slack-manager/reply"
                : Path(projectId!, "/reply");
            return await api.PrintPostWithOutputAsync(
                replyPath,
                new
                {
                    conversationId = ctx.GetValue(conversation),
                    threadTs = ctx.GetValue(replyTo),
                    dispatchRef = ctx.GetValue(dispatchRef),
                    connectionId = ctx.GetValue(connection),
                    triggeringMessageId = ctx.GetValue(triggeringMessage),
                    text = string.IsNullOrWhiteSpace(body) ? null : body,
                    imageUrl,
                    fileName,
                    fileContentBase64,
                },
                "json",
                tableShape: null);
        });
        return command;
    }
}
