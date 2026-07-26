namespace Mohist.Server.Infrastructure.Events;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EventPushAttribute : Attribute
{
    public required string Type { get; init; }

    public string? Identity { get; init; }
}
