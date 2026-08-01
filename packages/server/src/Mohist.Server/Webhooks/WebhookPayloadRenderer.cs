using System.Buffers;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Webhooks;

public sealed class WebhookPayloadRenderer
{
    public byte[] Render(CloudEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CloudEvent.JsonOptions.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("specversion", evt.SpecVersion);
            writer.WriteString("id", evt.Id);
            writer.WriteString("source", evt.Source.OriginalString);
            writer.WriteString("type", evt.Type);
            writer.WriteString("time", evt.Time);

            if (evt.Subject is not null)
                writer.WriteString("subject", evt.Subject);
            if (evt.DataContentType is not null)
                writer.WriteString("datacontenttype", evt.DataContentType);
            if (evt.Data is { } data)
            {
                writer.WritePropertyName("data");
                data.WriteTo(writer);
            }

            foreach (var extension in evt.Extensions)
            {
                if (IsCoreAttribute(extension.Key))
                    throw new InvalidOperationException($"CloudEvent extension '{extension.Key}' conflicts with a structured-mode core attribute.");
                writer.WritePropertyName(extension.Key);
                JsonSerializer.Serialize(writer, extension.Value, CloudEvent.JsonOptions);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsCoreAttribute(string attribute) => attribute is
        "specversion" or "id" or "source" or "type" or "time" or "subject" or "datacontenttype" or "data";
}
