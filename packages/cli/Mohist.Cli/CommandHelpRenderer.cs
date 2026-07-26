using System.CommandLine;
using System.Text;

namespace Mohist.Cli;

internal static class CommandHelpRenderer
{
    public static void RenderRoot(TextWriter writer, RootCommand root)
    {
        var capabilities = EnumerateVisibleTopLevel(root)
            .Select(cmd => (Command: cmd, Presentation: CommandPresentationCatalog.Get(cmd)))
            .Where(pair => pair.Presentation is not null)
            .ToArray();

        var byCapability = capabilities
            .GroupBy(pair => pair.Presentation!.Capability)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Command.Name, StringComparer.Ordinal).ToArray());

        writer.WriteLine("mo — Mohist command line");
        writer.WriteLine();
        writer.WriteLine("USAGE");
        writer.WriteLine("    mo <area> [<subarea>] <action> [target] [flags]");
        writer.WriteLine("    mo help <output|environment|exit-codes>");
        writer.WriteLine();
        writer.WriteLine("CAPABILITIES");
        foreach (var capability in Enum.GetValues<CommandCapability>())
        {
            if (!byCapability.TryGetValue(capability, out var entries) || entries.Length == 0)
                continue;
            writer.WriteLine($"    {capability}");
            foreach (var (cmd, presentation) in entries)
            {
                var summary = cmd.Name == "skill" ? "Manage coder agent tooling" : presentation!.Summary;
                writer.WriteLine($"  {cmd.Name,-14} {summary}");
            }
            writer.WriteLine();
        }

        writer.WriteLine("EXAMPLES");
        writer.WriteLine("    Discover Issue commands:    mo issue --help");
        writer.WriteLine("    Read a specific Issue:      mo issue view 42");
        writer.WriteLine("    Recover an archived Issue:  mo issue restore 42");
        writer.WriteLine();
        writer.WriteLine("FURTHER HELP");
        writer.WriteLine("    mo help <topic>      Read a shared rule (output, environment, exit-codes).");
        writer.WriteLine("    mo <area> --help     Show the command group's actions and resource boundary.");
        writer.WriteLine("    mo <area> <action> --help");
        writer.WriteLine("                        Show the exact invocation, options, and JSON fields.");
        writer.WriteLine("    docs                 See docs/cli-reference.md.");
    }

    public static void RenderGroup(TextWriter writer, Command group, string[] invocationPath)
    {
        var presentation = CommandPresentationCatalog.Get(group);
        writer.WriteLine(presentation?.Summary ?? group.Description ?? group.Name);
        writer.WriteLine();

        if (presentation?.Boundary is { } boundary)
        {
            writer.WriteLine("BOUNDARY");
            writer.WriteLine($"    {Wrap(boundary, 76)}");
            writer.WriteLine();
        }

        writer.WriteLine("USAGE");
        writer.WriteLine($"    {FormatGroupUsage(invocationPath)}");
        writer.WriteLine();

        var visible = EnumerateVisible(group).ToArray();
        if (visible.Length > 0)
        {
            writer.WriteLine("ACTIONS");
            foreach (var action in visible)
            {
                var summary = CommandPresentationCatalog.Get(action)?.Summary
                    ?? action.Description
                    ?? string.Empty;
                writer.WriteLine($"  {action.Name,-14} {summary}");
            }
            writer.WriteLine();
        }

        if (presentation?.SeeAlso is { } seeAlso)
        {
            writer.WriteLine("SEE ALSO");
            writer.WriteLine($"    {seeAlso}");
            writer.WriteLine();
        }

        writer.WriteLine("FURTHER HELP");
        writer.WriteLine($"    mo {string.Join(" ", invocationPath)} <action> --help");
        writer.WriteLine("                        Show the exact invocation, options, and JSON fields.");
    }

    public static void RenderLeaf(TextWriter writer, Command leaf, string[] invocationPath)
    {
        var presentation = CommandPresentationCatalog.Get(leaf);
        writer.WriteLine(presentation?.Summary ?? leaf.Description ?? leaf.Name);
        writer.WriteLine();

        if (presentation?.Boundary is { } boundary)
        {
            writer.WriteLine("BOUNDARY");
            writer.WriteLine($"    {Wrap(boundary, 76)}");
            writer.WriteLine();
        }

        writer.WriteLine("Usage:");
        writer.WriteLine($"    {FormatUsage(leaf, invocationPath)}");
        writer.WriteLine();

        if (leaf.Arguments.Count > 0)
        {
            writer.WriteLine("ARGUMENTS");
            foreach (var arg in leaf.Arguments)
                writer.WriteLine($"    {FormatSymbol(arg)}");
            writer.WriteLine();
        }

        var visibleOptions = leaf.Options
            .Where(o => !IsHidden(o))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ToArray();
        if (visibleOptions.Length > 0)
        {
            writer.WriteLine("OPTIONS");
            foreach (var opt in visibleOptions)
                writer.WriteLine($"    {FormatSymbol(opt)}");
            writer.WriteLine();
        }

        if (presentation?.Note is { } note)
        {
            writer.WriteLine("NOTE");
            writer.WriteLine($"    {Wrap(note, 76)}");
            writer.WriteLine();
        }

        if (presentation?.Examples is { Count: > 0 } examples)
        {
            writer.WriteLine("EXAMPLES");
            foreach (var example in examples.Take(3))
                writer.WriteLine($"    {example}");
            writer.WriteLine();
        }

        if (HasJsonSelection(leaf))
        {
            writer.WriteLine("JSON FIELDS");
            if (presentation?.JsonFields is { Count: > 0 } fields)
                writer.WriteLine($"    {string.Join(", ", fields)}");
            writer.WriteLine("    Run with --json (no value) to list the fields accepted by this command.");
            writer.WriteLine();
        }

        if (presentation?.SeeAlso is { } seeAlso)
        {
            writer.WriteLine("SEE ALSO");
            writer.WriteLine($"    {seeAlso}");
            writer.WriteLine();
        }
    }

    public static void RenderTopic(TextWriter writer, string topic)
    {
        if (!CommandHelpTopics.TryGet(topic, out var body))
        {
            RenderUnknownTopicUsage(writer, topic);
            return;
        }

        writer.Write(body);
    }

    public static void RenderUnknownTopicUsage(TextWriter writer, string? requested)
    {
        writer.WriteLine($"Usage: mo help <{string.Join("|", CommandHelpTopics.Names)}>");
        writer.WriteLine();
        if (requested is not null && !string.IsNullOrWhiteSpace(requested))
            writer.WriteLine($"Unknown help topic: {requested}");
    }

    public static void RenderGroupUsage(TextWriter writer, Command group, string[] invocationPath)
    {
        writer.WriteLine($"Usage: {FormatUsage(group, invocationPath)}");
        writer.WriteLine();
        writer.WriteLine($"Run `mo {string.Join(" ", invocationPath)} --help` for the full help.");
    }

    public static void RenderUnknownAreaUsage(TextWriter writer, string requested)
    {
        writer.WriteLine($"Unknown command or area: {requested}");
        writer.WriteLine();
        writer.WriteLine("Usage: mo <area> [<subarea>] <action> [target] [flags]");
        writer.WriteLine("Run `mo --help` for the list of available areas.");
    }

    public static IEnumerable<Command> EnumerateVisibleTopLevel(Command root)
    {
        foreach (var sub in root.Subcommands)
        {
            if (sub.Hidden) continue;
            yield return sub;
        }
    }

    public static IEnumerable<Command> EnumerateVisible(Command command)
    {
        foreach (var sub in command.Subcommands)
        {
            if (sub.Hidden) continue;
            yield return sub;
        }
    }

    public static bool IsLeaf(Command command) => !EnumerateVisible(command).Any();

    private static string FormatUsage(Command command, string[] invocationPath)
    {
        var parts = new List<string>(invocationPath.Length + 4) { "mo" };
        parts.AddRange(invocationPath);
        foreach (var argument in command.Arguments)
        {
            var symbol = $"<{argument.Name}>";
            parts.Add(argument.Arity.MinimumNumberOfValues > 0 ? symbol : $"[{symbol}]");
        }
        return string.Join(" ", parts) + " [flags]";
    }

    private static string FormatSymbol(Symbol symbol)
    {
        var sb = new StringBuilder();
        var name = symbol is Option && !symbol.Name.StartsWith("-", StringComparison.Ordinal)
            ? $"--{symbol.Name}"
            : symbol.Name;
        sb.Append(name.PadRight(20));
        sb.Append(symbol.Description ?? string.Empty);
        if (symbol is Argument argument && argument.Arity.MinimumNumberOfValues > 0)
            sb.Append(" (required)");
        if (symbol is Option option && option.Required)
            sb.Append(" (required)");
        if (symbol is Option optionWithDefault && optionWithDefault.HasDefaultValue)
        {
            var defaultValue = optionWithDefault.GetDefaultValue();
            if (defaultValue is not null)
                sb.Append($" (default: {defaultValue})");
        }
        return sb.ToString();
    }

    private static string FormatGroupUsage(string[] invocationPath) =>
        $"mo {string.Join(" ", invocationPath)} [<action>] [<resource>] [flags]";

    private static bool HasJsonSelection(Command leaf) =>
        leaf.Options.Any(o => string.Equals(o.Name.TrimStart('-'), "json", StringComparison.Ordinal));

    private static bool IsHidden(Option option) => option.Hidden;

    private static string Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = new StringBuilder();
        var line = new StringBuilder();
        var prefix = "    ";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0)
            {
                line.Append(word);
                continue;
            }
            if (line.Length + 1 + word.Length > width)
            {
                if (result.Length > 0) result.AppendLine();
                result.Append(prefix);
                result.Append(line.ToString());
                line.Clear();
                line.Append(word);
                continue;
            }
            line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
        {
            if (result.Length > 0) result.AppendLine();
            result.Append(prefix);
            result.Append(line.ToString());
        }
        return result.ToString();
    }
}
