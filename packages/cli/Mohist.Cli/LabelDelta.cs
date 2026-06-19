using System.Text.RegularExpressions;

namespace Mohist.Cli;

internal static class LabelDelta
{
    public const string KeyValidationPattern = "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$";
    private static readonly Regex KeyPattern =
        new(KeyValidationPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public enum Operation
    {
        Set,
        Remove,
    }

    public readonly record struct Entry(Operation Op, string Key, string? Value)
    {
        public bool IsRemove => Op == Operation.Remove;
        public bool IsSet => Op == Operation.Set;
    }

    public readonly record struct ParseResult(Entry[] Entries, string? Error)
    {
        public bool IsValid => Error is null;

        public static ParseResult Ok(Entry[] entries) => new(entries, null);

        public static ParseResult Fail(string error) => new([], error);
    }

    public static ParseResult Parse(string[]? tokens)
    {
        if (tokens is null || tokens.Length == 0)
            return ParseResult.Ok([]);

        var entries = new List<Entry>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrEmpty(token))
                return ParseResult.Fail("Label token must not be empty");

            if (token[0] == '-')
            {
                var key = token[1..];
                if (key.Length == 0)
                    return ParseResult.Fail("Label removal token '-' requires a key (e.g. '-key')");
                var keyError = ValidateKey(key);
                if (keyError is not null)
                    return ParseResult.Fail(keyError);
                entries.Add(new Entry(Operation.Remove, key, null));
                continue;
            }

            var eq = token.IndexOf('=');
            if (eq <= 0)
                return ParseResult.Fail(
                    $"Invalid label token '{token}': expected 'key=value' to set or '-key' to remove");

            var tokenKey = token[..eq];
            var tokenValue = token[(eq + 1)..];

            var keyErr = ValidateKey(tokenKey);
            if (keyErr is not null)
                return ParseResult.Fail(keyErr);

            if (string.IsNullOrWhiteSpace(tokenValue))
                return ParseResult.Fail(
                    $"Invalid label value for key '{tokenKey}': value must be a non-empty, non-whitespace string");

            entries.Add(new Entry(Operation.Set, tokenKey, tokenValue));
        }

        return ParseResult.Ok(entries.ToArray());
    }

    public static IReadOnlyDictionary<string, string> Apply(
        IEnumerable<Entry> entries,
        IReadOnlyDictionary<string, string>? current)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (current is not null)
        {
            foreach (var (k, v) in current)
                result[k] = v;
        }

        foreach (var entry in entries)
        {
            if (entry.IsRemove)
            {
                result.Remove(entry.Key);
            }
            else
            {
                result[entry.Key] = entry.Value!;
            }
        }

        return result;
    }

    public static string? ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return $"Issue label key is required and must match {KeyValidationPattern}";
        if (!KeyPattern.IsMatch(key))
            return $"Issue label key '{key}' is invalid; keys must match {KeyValidationPattern} (lowercase alphanumerics with optional interior dashes)";
        return null;
    }

    public static string? ValidateFilterToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return "Label filter must not be empty";
        var eq = token.IndexOf('=');
        if (eq <= 0)
            return $"Invalid label filter '{token}': expected 'key=value' (e.g. -l stream=frontend)";
        var key = token[..eq];
        var value = token[(eq + 1)..];
        var keyError = ValidateKey(key);
        if (keyError is not null) return keyError;
        if (string.IsNullOrWhiteSpace(value))
            return $"Invalid label filter '{token}': value must be a non-empty string";
        return null;
    }

    public static string? ValidateFilterTokens(string[]? tokens)
    {
        if (tokens is null || tokens.Length == 0) return null;
        for (var i = 0; i < tokens.Length; i++)
        {
            var err = ValidateFilterToken(tokens[i]);
            if (err is not null) return err;
        }
        return null;
    }
}
