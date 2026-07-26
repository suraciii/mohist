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

internal sealed record JsonFieldGroup(string Invocation, IReadOnlyList<string> Fields);

internal sealed record CommandPresentation(
    CommandCapability Capability,
    string Summary,
    string? Boundary = null,
    string? SeeAlso = null,
    string? Note = null,
    IReadOnlyList<string>? Examples = null,
    IReadOnlyList<string>? JsonFields = null,
    IReadOnlyList<JsonFieldGroup>? JsonFieldGroups = null);

internal static class CommandPresentationCatalog
{
    private static readonly ConditionalWeakTable<Command, CommandPresentation> Table = new();
    private static readonly ConditionalWeakTable<Option, ResourceDescriptor> JsonFields = new();

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

    public static void AttachJsonFields(Option option, ResourceDescriptor descriptor)
    {
        JsonFields.Remove(option);
        JsonFields.Add(option, descriptor);
    }

    public static IReadOnlyList<string>? GetJsonFields(Option option) =>
        JsonFields.TryGetValue(option, out var descriptor) ? descriptor.Fields : null;
}
