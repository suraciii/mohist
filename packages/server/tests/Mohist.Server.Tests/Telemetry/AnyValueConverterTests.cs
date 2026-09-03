using System.Text.Json;
using Mohist.Server.Otel.OtlpJson;
using Xunit;

namespace Mohist.Server.Tests.Telemetry;

[Trait("level", "L0")]
public class AnyValueConverterTests
{
    private static readonly JsonSerializerOptions Options = OtlpJsonSerializer.Options();

    [Fact]
    public void StringValue_DeserializesToStringKind()
    {
        var result = JsonSerializer.Deserialize<AnyValue>("""{"stringValue":"hello"}""", Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.String, result!.Kind);
        Assert.Equal("hello", result.StringValue);
    }

    [Fact]
    public void BoolValue_DeserializesToBoolKind()
    {
        var result = JsonSerializer.Deserialize<AnyValue>("""{"boolValue":true}""", Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Bool, result!.Kind);
        Assert.True(result.BoolValue);
    }

    [Fact]
    public void IntValue_DeserializesToIntKind()
    {
        var result = JsonSerializer.Deserialize<AnyValue>("""{"intValue":"42"}""", Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Int, result!.Kind);
        Assert.Equal(42L, result.IntValue);
    }

    [Fact]
    public void IntValue_NumberFormIsAlsoAccepted()
    {
        var result = JsonSerializer.Deserialize<AnyValue>("""{"intValue":7}""", Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Int, result!.Kind);
        Assert.Equal(7L, result.IntValue);
    }

    [Fact]
    public void DoubleValue_DeserializesToDoubleKind()
    {
        var result = JsonSerializer.Deserialize<AnyValue>("""{"doubleValue":3.14}""", Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Double, result!.Kind);
        Assert.Equal(3.14, result.DoubleValue);
    }

    [Fact]
    public void ArrayValue_DeserializesRecursivelyToArrayKind()
    {
        const string json = """{"arrayValue":{"values":[{"stringValue":"a"},{"intValue":"1"}]}}""";

        var result = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Array, result!.Kind);
        Assert.NotNull(result.ArrayValue);
        Assert.Equal(2, result.ArrayValue!.Count);
        Assert.Equal(AnyValueKind.String, result.ArrayValue[0].Kind);
        Assert.Equal("a", result.ArrayValue[0].StringValue);
        Assert.Equal(AnyValueKind.Int, result.ArrayValue[1].Kind);
        Assert.Equal(1L, result.ArrayValue[1].IntValue);
    }

    [Fact]
    public void KvlistValue_DeserializesToKeyValueListKind()
    {
        const string json = """
            {
              "kvlistValue": {
                "values": [
                  {"key":"service.name","value":{"stringValue":"svc"}},
                  {"key":"retry","value":{"boolValue":true}}
                ]
              }
            }
            """;

        var result = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.KeyValueList, result!.Kind);
        Assert.NotNull(result.KvlistValue);
        Assert.Equal(2, result.KvlistValue!.Count);
        Assert.Equal("service.name", result.KvlistValue[0].Key);
        Assert.Equal("svc", result.KvlistValue[0].Value!.StringValue);
        Assert.True(result.KvlistValue[1].Value!.BoolValue);
    }

    [Fact]
    public void BytesValue_DeserializesBase64ToBytesKind()
    {
        const string json = """{"bytesValue":"aGVsbG8="}""";

        var result = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.Bytes, result!.Kind);
        Assert.NotNull(result.BytesValue);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("hello"), result.BytesValue);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        const string json = """{"stringValue":"ok","futureField":42}""";

        var result = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(AnyValueKind.String, result!.Kind);
        Assert.Equal("ok", result.StringValue);
    }

    [Fact]
    public void EmptyObject_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AnyValue>("""{}""", Options));
    }

    [Fact]
    public void Write_RoundTripsStringValue()
    {
        var value = new AnyValue { Kind = AnyValueKind.String, StringValue = "round-trip" };

        var json = JsonSerializer.Serialize(value, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("stringValue", out var prop));
        Assert.Equal("round-trip", prop.GetString());
    }

    [Fact]
    public void Write_RoundTripsIntValue()
    {
        var value = new AnyValue { Kind = AnyValueKind.Int, IntValue = 99 };

        var json = JsonSerializer.Serialize(value, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("intValue", out var prop));
        Assert.Equal(99L, prop.GetInt64());
    }

    [Fact]
    public void Write_RoundTripsArrayValue()
    {
        var value = new AnyValue
        {
            Kind = AnyValueKind.Array,
            ArrayValue = new List<AnyValue>
            {
                new() { Kind = AnyValueKind.String, StringValue = "x" },
            },
        };

        var json = JsonSerializer.Serialize(value, Options);
        var roundTripped = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(AnyValueKind.Array, roundTripped!.Kind);
        Assert.NotNull(roundTripped.ArrayValue);
        Assert.Single(roundTripped.ArrayValue!);
        Assert.Equal("x", roundTripped.ArrayValue![0].StringValue);
    }

    [Fact]
    public void Write_RoundTripsKvlistValue()
    {
        var value = new AnyValue
        {
            Kind = AnyValueKind.KeyValueList,
            KvlistValue = new List<KeyValue>
            {
                new() { Key = "k", Value = new AnyValue { Kind = AnyValueKind.Int, IntValue = 5 } },
            },
        };

        var json = JsonSerializer.Serialize(value, Options);
        var roundTripped = JsonSerializer.Deserialize<AnyValue>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(AnyValueKind.KeyValueList, roundTripped!.Kind);
        Assert.NotNull(roundTripped.KvlistValue);
        Assert.Single(roundTripped.KvlistValue!);
        Assert.Equal("k", roundTripped.KvlistValue![0].Key);
        Assert.Equal(5L, roundTripped.KvlistValue![0].Value!.IntValue);
    }
}
