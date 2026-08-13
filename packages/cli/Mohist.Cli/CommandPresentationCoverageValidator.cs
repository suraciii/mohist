using System.CommandLine;

namespace Mohist.Cli;

internal sealed record CommandPresentationCoverageDiagnostic(
    string InvocationPath,
    string Message);

internal static class CommandPresentationCoverageValidator
{
    public static IReadOnlyList<CommandPresentationCoverageDiagnostic> Validate(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var diagnostics = new List<CommandPresentationCoverageDiagnostic>();
        VisitChildren(root, ["mo"], diagnostics);
        return diagnostics
            .OrderBy(diagnostic => diagnostic.InvocationPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static void VisitChildren(
        Command parent,
        IReadOnlyList<string> parentPath,
        ICollection<CommandPresentationCoverageDiagnostic> diagnostics)
    {
        foreach (var command in parent.Subcommands)
        {
            var path = parentPath.Append(command.Name).ToArray();

            if (!command.Hidden && TryBuildDiagnostic(command, parent is RootCommand, path, out var diagnostic))
                diagnostics.Add(diagnostic);

            VisitChildren(command, path, diagnostics);
        }
    }

    private static bool TryBuildDiagnostic(
        Command command,
        bool isRootChild,
        IReadOnlyList<string> path,
        out CommandPresentationCoverageDiagnostic diagnostic)
    {
        var presentation = CommandPresentationCatalog.Get(command);
        var hasSummary = presentation is not null && !string.IsNullOrWhiteSpace(presentation.Summary);
        var hasCapability = presentation?.Capability is { } capability && Enum.IsDefined(capability);
        if (hasSummary && (!isRootChild || hasCapability))
        {
            diagnostic = null!;
            return false;
        }

        var missingRequirements = new List<string>();
        if (!hasSummary)
            missingRequirements.Add("explicit non-empty presentation");
        if (isRootChild && !hasCapability)
            missingRequirements.Add("capability classification");

        var invocationPath = string.Join(' ', path);
        diagnostic = new CommandPresentationCoverageDiagnostic(
            invocationPath,
            $"{invocationPath}: missing {string.Join(" and ", missingRequirements)}");
        return true;
    }
}
