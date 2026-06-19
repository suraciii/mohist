namespace Mohist.Server.Epic.Domain;

public readonly record struct EpicPriority(string Value)
{
    public static EpicPriority Default { get; } = new("p2");

    public static EpicPriority From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Default : new EpicPriority(value);

    public override string ToString() => Value;
}