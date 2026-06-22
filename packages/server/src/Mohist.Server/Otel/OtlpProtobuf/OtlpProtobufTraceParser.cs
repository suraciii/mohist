using System.Globalization;
using Google.Protobuf;
using Mohist.Server.Otel.OtlpJson;

namespace Mohist.Server.Otel.OtlpProtobuf;

internal static class OtlpProtobufTraceParser
{
    public static OtlpTraceRequest Parse(byte[] payload)
    {
        var input = new CodedInputStream(payload);
        var request = new OtlpTraceRequest { ResourceSpans = new List<ResourceSpans>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                request.ResourceSpans.Add(ReadResourceSpans(input.ReadBytes()));
            }
            else
            {
                input.SkipLastField();
            }
        }

        return request;
    }

    private static ResourceSpans ReadResourceSpans(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var resourceSpans = new ResourceSpans { ScopeSpans = new List<ScopeSpans>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                resourceSpans.Resource = ReadResource(input.ReadBytes());
            }
            else if (tag == 18)
            {
                resourceSpans.ScopeSpans.Add(ReadScopeSpans(input.ReadBytes()));
            }
            else if (tag == 26)
            {
                resourceSpans.ScopeSpans.Add(ReadInstrumentationLibrarySpans(input.ReadBytes()));
            }
            else if (tag == 34)
            {
                resourceSpans.SchemaUrl = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return resourceSpans;
    }

    private static Resource ReadResource(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var resource = new Resource { Attributes = new List<KeyValue>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                resource.Attributes.Add(ReadKeyValue(input.ReadBytes()));
            }
            else if (tag == 16)
            {
                resource.DroppedAttributesCount = input.ReadUInt32();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return resource;
    }

    private static ScopeSpans ReadScopeSpans(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var scopeSpans = new ScopeSpans { Spans = new List<Span>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                scopeSpans.Scope = ReadInstrumentationScope(input.ReadBytes());
            }
            else if (tag == 18)
            {
                scopeSpans.Spans.Add(ReadSpan(input.ReadBytes()));
            }
            else if (tag == 26)
            {
                scopeSpans.SchemaUrl = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return scopeSpans;
    }

    private static ScopeSpans ReadInstrumentationLibrarySpans(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var scopeSpans = new ScopeSpans { Spans = new List<Span>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                scopeSpans.Scope = ReadInstrumentationScope(input.ReadBytes());
            }
            else if (tag == 18)
            {
                scopeSpans.Spans.Add(ReadSpan(input.ReadBytes()));
            }
            else if (tag == 26)
            {
                scopeSpans.SchemaUrl = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return scopeSpans;
    }

    private static InstrumentationScope ReadInstrumentationScope(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var scope = new InstrumentationScope { Attributes = new List<KeyValue>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                scope.Name = input.ReadString();
            }
            else if (tag == 18)
            {
                scope.Version = input.ReadString();
            }
            else if (tag == 26)
            {
                scope.Attributes.Add(ReadKeyValue(input.ReadBytes()));
            }
            else if (tag == 32)
            {
                scope.DroppedAttributesCount = input.ReadUInt32();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return scope;
    }

    private static Span ReadSpan(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var span = new Span { Attributes = new List<KeyValue>() };

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            switch (tag)
            {
                case 10:
                    span.TraceId = ToHex(input.ReadBytes());
                    break;
                case 18:
                    span.SpanId = ToHex(input.ReadBytes());
                    break;
                case 26:
                    span.TraceState = input.ReadString();
                    break;
                case 34:
                    span.ParentSpanId = ToHex(input.ReadBytes());
                    break;
                case 42:
                    span.Name = input.ReadString();
                    break;
                case 48:
                    span.Kind = input.ReadEnum();
                    break;
                case 56:
                    span.StartTimeUnixNano = input.ReadUInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 57:
                    span.StartTimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 65:
                    span.EndTimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 72:
                    span.EndTimeUnixNano = input.ReadUInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 81:
                    input.SkipLastField();
                    break;
                case 90:
                    span.Attributes.Add(ReadKeyValue(input.ReadBytes()));
                    break;
                case 122:
                    span.Status = ReadStatus(input.ReadBytes());
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return span;
    }

    private static Status ReadStatus(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var status = new Status();

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 18)
            {
                status.Message = input.ReadString();
            }
            else if (tag == 24)
            {
                status.Code = input.ReadEnum();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return status;
    }

    private static KeyValue ReadKeyValue(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var keyValue = new KeyValue();

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            if (tag == 10)
            {
                keyValue.Key = input.ReadString();
            }
            else if (tag == 18)
            {
                keyValue.Value = ReadAnyValue(input.ReadBytes());
            }
            else
            {
                input.SkipLastField();
            }
        }

        return keyValue;
    }

    private static AnyValue ReadAnyValue(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        var value = new AnyValue();

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            switch (tag)
            {
                case 10:
                    value.Kind = AnyValueKind.String;
                    value.StringValue = input.ReadString();
                    break;
                case 17:
                    value.Kind = AnyValueKind.Bool;
                    value.BoolValue = input.ReadBool();
                    break;
                case 24:
                    value.Kind = AnyValueKind.Int;
                    value.IntValue = input.ReadInt64();
                    break;
                case 33:
                    value.Kind = AnyValueKind.Double;
                    value.DoubleValue = input.ReadDouble();
                    break;
                case 42:
                    value.Kind = AnyValueKind.Bytes;
                    value.BytesValue = input.ReadBytes().ToByteArray();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return value;
    }

    private static string ToHex(ByteString bytes) => Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
}
