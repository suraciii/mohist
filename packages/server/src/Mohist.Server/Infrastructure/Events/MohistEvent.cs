using System.Text.Json;
using System.Threading.Channels;

namespace Mohist.Server.Infrastructure.Events;

public record MohistEvent(string EventName, object Data);
