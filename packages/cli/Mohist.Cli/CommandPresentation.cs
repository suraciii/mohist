using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Mohist.Cli;

internal enum CommandCapability
{
    Work,
    Automation,
    Operations,
    Tools,
}

internal sealed record CommandPresentation(
    CommandCapability Capability,
    string Summary,
    string? Boundary = null,
    string? SeeAlso = null,
    string? Note = null,
    IReadOnlyList<string>? Examples = null,
    IReadOnlyList<string>? JsonFields = null);

internal static class CommandPresentationCatalog
{
    private static readonly ConditionalWeakTable<Command, CommandPresentation> Table = new();

    public static void Attach(Command? command, CommandPresentation presentation)
    {
        if (command is null) return;
        if (Table.TryGetValue(command, out var existing))
        {
            if (ReferenceEquals(existing, presentation))
                return;
            Table.Remove(command);
        }
        Table.Add(command, presentation);
    }

    public static CommandPresentation? Get(Command? command) =>
        command is not null && Table.TryGetValue(command, out var presentation) ? presentation : null;

    public static bool Has(Command? command) => command is not null && Table.TryGetValue(command, out _);
}
