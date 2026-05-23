using System.Text.Json;
using System.Threading.Channels;

namespace Mohist.Server.Events;

public record MohistEvent(string EventName, object Data);
