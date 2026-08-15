using System.CommandLine;

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
    CommandCapability? Capability,
    string Summary,
    string? Boundary = null,
    string? SeeAlso = null,
    string? Note = null,
    IReadOnlyList<string>? Examples = null,
    IReadOnlyList<string>? JsonFields = null,
    IReadOnlyList<JsonFieldGroup>? JsonFieldGroups = null);

internal sealed class CliRootCommand(string description) : RootCommand(description)
{
    private readonly Dictionary<Command, CommandPresentation> _presentations =
        new(ReferenceEqualityComparer.Instance);

    internal void Attach(Command command, CommandPresentation presentation) =>
        _presentations[command] = presentation;

    internal CommandPresentation? Get(Command command) =>
        _presentations.TryGetValue(command, out var presentation) ? presentation : null;
}

internal sealed class CliJsonOption(string name, params string[] aliases) : Option<string?>(name, aliases)
{
    internal ResourceDescriptor? Descriptor { get; set; }
}

internal static class CommandPresentationCatalog
{
    public static void Attach(Command? command, CommandPresentation presentation)
    {
        if (command is null) return;
        var root = FindRoot(command)
            ?? throw new InvalidOperationException("Command presentation requires a CliRootCommand owner.");
        root.Attach(command, presentation);
    }

    public static CommandPresentation? Get(Command? command) =>
        command is not null ? FindRoot(command)?.Get(command) : null;

    public static CommandPresentation Require(Command command) =>
        Get(command) is { } presentation && !string.IsNullOrWhiteSpace(presentation.Summary)
            ? presentation
            : throw new InvalidOperationException($"Missing explicit help presentation for command '{command.Name}'.");

    public static CommandPresentation RequireRoot(Command command)
    {
        var presentation = Require(command);
        if (presentation.Capability is not { } capability || !Enum.IsDefined(capability))
            throw new InvalidOperationException($"Missing explicit help capability classification for command '{command.Name}'.");

        return presentation;
    }

    public static bool Has(Command? command) => Get(command) is not null;

    public static void AttachJsonFields(Option option, ResourceDescriptor descriptor)
    {
        if (option is not CliJsonOption jsonOption)
            throw new InvalidOperationException("JSON field metadata requires a CliJsonOption owner.");
        jsonOption.Descriptor = descriptor;
    }

    public static IReadOnlyList<string>? GetJsonFields(Option option) =>
        option is CliJsonOption { Descriptor: { } descriptor } ? descriptor.Fields : null;

    private static CliRootCommand? FindRoot(Command command)
    {
        Command? current = command;
        while (current is not null)
        {
            if (current is CliRootCommand root)
                return root;
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }
        return null;
    }
}
