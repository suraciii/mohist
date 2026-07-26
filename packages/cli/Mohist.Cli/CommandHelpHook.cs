using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class CommandHelpHook
{
    public static void Install(RootCommand root)
    {
        foreach (var cmd in EnumerateAll(root))
            ReplaceHelpOption(cmd);
    }

    public static Command BuildHelpCommand()
    {
        var help = new Command("help", "Read a shared rule (output, environment, exit-codes)");
        var topicArg = new Argument<string?>("topic")
        {
            Description = "Help topic (one of: output, environment, exit-codes)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        help.Arguments.Add(topicArg);
        help.SetAction(ctx => Task.FromResult(RunTopic(ctx, ctx.GetValue(topicArg))));
        return help;
    }

    private static int RunTopic(ParseResult ctx, string? topic)
    {
        var error = ctx.InvocationConfiguration.Error;
        if (string.IsNullOrWhiteSpace(topic))
        {
            CommandHelpRenderer.RenderUnknownTopicUsage(error, topic);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        if (!CommandHelpTopics.Names.Contains(topic, StringComparer.Ordinal))
        {
            CommandHelpRenderer.RenderUnknownTopicUsage(error, topic);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        CommandHelpRenderer.RenderTopic(ctx.InvocationConfiguration.Output, topic);
        return CliExitCode.For(CliExitOutcome.Success);
    }

    private static void ReplaceHelpOption(Command command)
    {
        var helpOption = command.Options.FirstOrDefault(o => o is { Name: "--help" });
        if (helpOption is null) return;

        var actionProp = helpOption.GetType().GetProperty("Action");
        if (actionProp is null || !actionProp.CanWrite) return;

        var currentAction = actionProp.GetValue(helpOption);
        if (currentAction is LocalHelpAction) return;

        actionProp.SetValue(helpOption, new LocalHelpAction());
    }

    public static int RenderNearestUsage(ParseResult parseResult, TextWriter error)
    {
        var nearest = FindNearestRenderable(parseResult);
        if (nearest is null)
        {
            CommandHelpRenderer.RenderUnknownAreaUsage(error, parseResult.Tokens.FirstOrDefault()?.Value ?? "<unknown>");
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        var path = BuildInvocationPath(nearest, parseResult);
        if (CommandHelpRenderer.IsLeaf(nearest))
        {
            CommandHelpRenderer.RenderLeaf(error, nearest, path);
        }
        else
        {
            CommandHelpRenderer.RenderGroupUsage(error, nearest, path);
        }
        return CliExitCode.For(CliExitOutcome.UsageFailure);
    }

    public static int RenderUsageFailure(ParseResult parseResult, TextWriter error, string diagnostic)
    {
        error.WriteLine(diagnostic);
        return RenderNearestUsage(parseResult, error);
    }

    private static Command? FindNearestRenderable(ParseResult parseResult)
    {
        var result = parseResult.CommandResult;
        while (result.Children.OfType<CommandResult>().FirstOrDefault() is { } child)
            result = child;

        var current = result.Command;
        while (current is not null && current is not RootCommand)
        {
            if (CommandPresentationCatalog.Get(current) is not null)
                return current;
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }
        return current is RootCommand ? current : null;
    }

    private static string[] BuildInvocationPath(Command target, ParseResult parseResult)
    {
        var stack = new Stack<string>();
        var current = target;
        while (current is not null && current is not RootCommand)
        {
            stack.Push(current.Name);
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }
        return stack.ToArray();
    }

    private static IEnumerable<Command> EnumerateAll(Command root)
    {
        yield return root;
        foreach (var sub in root.Subcommands)
            foreach (var c in EnumerateAll(sub))
                yield return c;
    }

    private sealed class LocalHelpAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            var output = parseResult.InvocationConfiguration.Output;
            var command = parseResult.CommandResult.Command;
            if (command is RootCommand root)
            {
                CommandHelpRenderer.RenderRoot(output, root);
                return CliExitCode.For(CliExitOutcome.Success);
            }

            var path = BuildInvocationPath(command, parseResult);
            if (CommandHelpRenderer.IsLeaf(command))
            {
                CommandHelpRenderer.RenderLeaf(output, command, path);
            }
            else
            {
                CommandHelpRenderer.RenderGroup(output, command, path);
            }
            return CliExitCode.For(CliExitOutcome.Success);
        }
    }
}
