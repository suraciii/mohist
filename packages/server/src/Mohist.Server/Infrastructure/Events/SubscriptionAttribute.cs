namespace Mohist.Server.Infrastructure.Events;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SubscriptionAttribute : Attribute
{
    public required string Type { get; init; }

    public string? Identity { get; init; }
}
