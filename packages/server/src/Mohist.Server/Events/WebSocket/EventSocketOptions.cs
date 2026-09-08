namespace Mohist.Server.Events.WebSocket;

public sealed class EventSocketOptions
{
    public const string SectionName = "EventSocket";

    public string[] TrustedProxyAddresses { get; set; } = [];
}
