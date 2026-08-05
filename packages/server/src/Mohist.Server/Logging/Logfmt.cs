using System.Globalization;
using System.Text;

namespace Mohist.Server.Logging;

internal static class Logfmt
{
    public static string Serialize(LogRecord record)
    {
        var builder = new StringBuilder();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Append(builder, keys, "time", FormatTime(record.Time));
        Append(builder, keys, "level", record.Level);
        Append(builder, keys, "msg", record.Message);
        Append(builder, keys, "service", record.Service);
        Append(builder, keys, "component", record.Component);

        if (record.Fields is not null)
        {
            foreach (var field in record.Fields)
            {
                if (string.IsNullOrEmpty(field.Key)
                    || field.Value is null
                    || IsReservedKey(field.Key))
                    continue;

                Append(builder, keys, field.Key, FormatValue(field.Value));
            }
        }

        Append(builder, keys, "exception", record.Exception);
        return builder.ToString();
    }

    public static bool TryParse(string line, out IReadOnlyDictionary<string, string> values)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (position < line.Length)
        {
            if (line[position] == '\n' || line[position] == '\r' || IsSeparator(line[position]))
            {
                values = parsed;
                return false;
            }

            var keyStart = position;
            while (position < line.Length && line[position] != '=' && !IsSeparator(line[position]))
                position++;

            if (position == keyStart || position >= line.Length || line[position] != '=')
            {
                values = parsed;
                return false;
            }

            var key = line[keyStart..position];
            if (!IsValidKey(key) || !parsed.TryAdd(key, string.Empty))
            {
                values = parsed;
                return false;
            }

            position++;
            if (position >= line.Length)
            {
                values = parsed;
                return false;
            }

            if (line[position] == '"')
            {
                if (!TryReadQuoted(line, ref position, out var quoted))
                {
                    values = parsed;
                    return false;
                }

                parsed[key] = quoted;
            }
            else
            {
                var valueStart = position;
                while (position < line.Length && !IsSeparator(line[position]))
                {
                    if (line[position] is '=' or '"' or '\\' or '\n' or '\r')
                    {
                        values = parsed;
                        return false;
                    }

                    position++;
                }

                if (position == valueStart)
                {
                    values = parsed;
                    return false;
                }

                parsed[key] = line[valueStart..position];
            }

            if (position < line.Length && !IsSeparator(line[position]))
            {
                values = parsed;
                return false;
            }

            while (position < line.Length && IsSeparator(line[position]))
                position++;
        }

        values = parsed;
        return parsed.ContainsKey("time")
            && parsed.ContainsKey("level")
            && parsed.ContainsKey("msg")
            && parsed.ContainsKey("service")
            && parsed.ContainsKey("component")
            && IsValidTime(parsed["time"])
            && IsValidLevel(parsed["level"]);
    }

    private static void Append(StringBuilder builder, HashSet<string> keys, string key, string? value)
    {
        if (string.IsNullOrEmpty(key) || value is null || !IsValidKey(key) || !keys.Add(key))
            return;

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(key).Append('=').Append(FormatValue(value));
    }

    private static string FormatValue(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatValue(string value)
    {
        if (!NeedsQuotes(value))
            return value;

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
            return true;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character)
                || char.IsControl(character)
                || character is '=' or '"' or '\\')
                return true;
        }

        return false;
    }

    private static bool TryReadQuoted(string line, ref int position, out string value)
    {
        var builder = new StringBuilder();
        position++;
        while (position < line.Length)
        {
            var character = line[position++];
            if (character == '"')
            {
                value = builder.ToString();
                return position == line.Length || IsSeparator(line[position]);
            }

            if (character != '\\')
            {
                if (character is '\n' or '\r')
                {
                    value = string.Empty;
                    return false;
                }

                builder.Append(character);
                continue;
            }

            if (position >= line.Length)
            {
                value = string.Empty;
                return false;
            }

            var escaped = line[position++];
            switch (escaped)
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '"':
                    builder.Append('"');
                    break;
                case 'u' when TryReadHex(line, ref position, out var codePoint):
                    builder.Append((char)codePoint);
                    break;
                default:
                    value = string.Empty;
                    return false;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadHex(string line, ref int position, out int value)
    {
        value = 0;
        if (line.Length - position < 4)
            return false;

        for (var index = 0; index < 4; index++)
        {
            var digit = line[position++];
            var number = digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => -1,
            };
            if (number < 0)
                return false;
            value = (value * 16) + number;
        }

        return true;
    }

    private static bool IsValidKey(string key) => key.All(character =>
        !char.IsWhiteSpace(character) && character is not '=' and not '"' and not '\\');

    private static bool IsSeparator(char character) => character == ' ';

    private static bool IsReservedKey(string key) => key is
        "time" or "level" or "msg" or "service" or "component" or "exception";

    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool IsValidTime(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private static bool IsValidLevel(string value) => value is
        "TRACE" or "DEBUG" or "INFO" or "WARN" or "ERROR" or "FATAL";
}
