using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Mohist.Cli;

internal static class SystemdUnitParser
{
    internal const string ServerUnit = "mohist.service";
    internal const string RunnerUnit = "mohist-runner.service";

    internal const string NotRunning = "<not running>";
    internal const string NotInstalled = "<not installed>";
    internal const string NotAGitRepo = "<not a git repo>";
    internal const string Unknown = "<unknown>";

    internal const string ShowProperties =
        "ActiveState,MainPID,ExecMainStartTimestamp,FragmentPath,WorkingDirectory,ExecStart,Environment";

    internal sealed record SystemdUnitFields(string? WorkingDirectory, string? ExecStart);

    internal sealed record RunnerIdSetting(string? RunnerId, string? Error);

    internal static SystemdUnitFields ParseSystemdUnit(string content)
    {
        string? workingDir = null;
        string? execStart = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith('['))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (string.Equals(key, "WorkingDirectory", StringComparison.Ordinal))
                workingDir = value;
            else if (string.Equals(key, "ExecStart", StringComparison.Ordinal))
                execStart = value;
        }
        return new SystemdUnitFields(workingDir, execStart);
    }

    internal static Dictionary<string, string> ParseSystemdShow(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            map[key] = value;
        }
        return map;
    }

    internal static string? ParseSystemdValue(string output, string key)
    {
        var map = ParseSystemdShow(output);
        return map.TryGetValue(key, out var v) ? v : null;
    }

    internal static Dictionary<string, string> ParseSystemdEnvironment(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!line.StartsWith("Environment=", StringComparison.Ordinal))
                continue;
            var payload = line["Environment=".Length..];
            foreach (var pair in TokenizeEnvironmentAssignments(payload))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                if (key.Length == 0)
                    continue;
                if (map.TryGetValue(key, out var existing))
                    map[key] = existing + " " + value;
                else
                    map[key] = value;
            }
        }
        return map;
    }

    internal static RunnerIdSetting ReadRunnerIdSetting(string content)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("Environment=", StringComparison.Ordinal))
                continue;

            foreach (var value in ReadRunnerIdAssignments(line["Environment=".Length..]))
            {
                found = true;
                if (value.Length == 0)
                    return new RunnerIdSetting(null, "runner launch identity is empty");
                values.Add(value);
            }
        }

        if (!found)
            return new RunnerIdSetting(null, null);
        if (values.Count != 1)
            return new RunnerIdSetting(null, "runner launch identity is ambiguous");
        return new RunnerIdSetting(values.Single(), null);
    }

    private static IEnumerable<string> ReadRunnerIdAssignments(string value)
    {
        var values = new List<string>();
        var name = new StringBuilder();
        StringBuilder? runnerId = null;
        var readingName = true;
        var inSingle = false;
        var inDouble = false;

        void CompleteAssignment()
        {
            if (runnerId is not null)
                values.Add(runnerId.ToString());
            name.Clear();
            runnerId = null;
            readingName = true;
        }

        foreach (var ch in value)
        {
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }
            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                CompleteAssignment();
                continue;
            }

            if (readingName)
            {
                if (ch == '=')
                {
                    readingName = false;
                    if (name.ToString() is "RUNNER_ID" or "RunnerId")
                        runnerId = new StringBuilder();
                    continue;
                }

                name.Append(ch);
            }
            else
            {
                runnerId?.Append(ch);
            }
        }

        CompleteAssignment();
        return values;
    }

    internal static IEnumerable<string> TokenizeEnvironmentAssignments(string value)
    {
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle)
            {
                if (inDouble)
                {
                    inDouble = false;
                    continue;
                }
                inDouble = true;
                continue;
            }
            else if (c == '(' && !inSingle && !inDouble) depth++;
            else if (c == ')' && !inSingle && !inDouble && depth > 0) depth--;
            else if (c == ' ' && !inSingle && !inDouble && depth == 0)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    internal static bool TryParseSystemdTimestamp(string value, out DateTimeOffset result)
    {
        value = value.Trim();
        if (string.IsNullOrEmpty(value))
        {
            result = default;
            return false;
        }
        var normalized = NormalizeTimestampForParsing(value);
        var formats = new[]
        {
            "ddd MMM d HH:mm:ss yyyy zzz",
            "ddd MMM  d HH:mm:ss yyyy zzz",
            "ddd MMM d HH:mm:ss yyyy",
            "ddd MMM  d HH:mm:ss yyyy",
            "ddd yyyy-MM-dd HH:mm:ss zzz",
            "ddd yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss zzz",
            "yyyy-MM-ddTHH:mm:ss",
        };
        if (DateTimeOffset.TryParseExact(normalized, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out result))
            return true;
        if (DateTimeOffset.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out result))
            return true;
        result = default;
        return false;
    }

    private static readonly Regex TimestampRegex =
        new(@"^(?<dow>[A-Za-z]{3,9}\s+)?(?<date>\d{4}-\d{2}-\d{2})(?:\s+(?<time>\d{2}:\d{2}:\d{2}))?(?:\s+(?<tz>\S+))?$",
            RegexOptions.Compiled);

    private static string NormalizeTimestampForParsing(string value)
    {
        var match = TimestampRegex.Match(value);
        if (!match.Success)
            return value;
        var datePart = match.Groups["date"].Value;
        var timePart = match.Groups["time"].Success ? match.Groups["time"].Value : "00:00:00";
        var tzPart = match.Groups["tz"].Success ? match.Groups["tz"].Value : "UTC";
        if (string.Equals(tzPart, "UTC", StringComparison.OrdinalIgnoreCase))
            tzPart = "+00:00";
        else if (tzPart == "Z")
            tzPart = "+00:00";
        if (datePart.Length == 0)
            return value;
        return $"{datePart} {timePart} {tzPart}";
    }

    internal static bool TryParseUptimeToSeconds(string text, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var match = Regex.Match(
            text,
            @"^(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$");
        if (!match.Success || (!match.Groups["d"].Success && !match.Groups["h"].Success
            && !match.Groups["m"].Success && !match.Groups["s"].Success))
            return false;
        long d = match.Groups["d"].Success ? long.Parse(match.Groups["d"].Value) : 0;
        long h = match.Groups["h"].Success ? long.Parse(match.Groups["h"].Value) : 0;
        long m = match.Groups["m"].Success ? long.Parse(match.Groups["m"].Value) : 0;
        long s = match.Groups["s"].Success ? long.Parse(match.Groups["s"].Value) : 0;
        seconds = d * 86_400 + h * 3_600 + m * 60 + s;
        return true;
    }

    internal static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d{uptime.Hours}h";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h{uptime.Minutes}m";
        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m";
        return $"{(int)uptime.TotalSeconds}s";
    }

    internal static string? TryGetUptimeFromProc(int pid, IFileSystem fileSystem)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return null;
            var statPath = $"/proc/{pid}/stat";
            if (!fileSystem.Exists(statPath))
                return null;
            var stat = fileSystem.ReadAllText(statPath);
            var startTimeTicks = ParseStartTimeFromProcStat(stat);
            if (startTimeTicks is null)
                return null;

            var uptimeFile = "/proc/uptime";
            if (!fileSystem.Exists(uptimeFile))
                return null;
            var uptimeText = fileSystem.ReadAllText(uptimeFile).Split(' ').FirstOrDefault();
            if (uptimeText is null || !double.TryParse(uptimeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var uptimeSeconds))
                return null;
            var startSecondsAgo = uptimeSeconds - startTimeTicks.Value / (double)System.Diagnostics.Stopwatch.Frequency;
            if (startSecondsAgo < 0)
                startSecondsAgo = 0;
            return FormatUptime(TimeSpan.FromSeconds(startSecondsAgo));
        }
        catch
        {
            return null;
        }
    }

    internal static long? ParseStartTimeFromProcStat(string stat)
    {
        var lastParen = stat.LastIndexOf(')');
        if (lastParen < 0)
            return null;
        var afterCmd = stat.IndexOf(' ', lastParen + 1);
        if (afterCmd < 0)
            return null;
        var fields = stat[(afterCmd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20)
            return null;
        var startTimeText = fields[19];
        return long.TryParse(startTimeText, out var startTime) ? startTime : null;
    }

    internal static InfoServiceStatus? BuildStatusFromProperties(
        Dictionary<string, string> properties,
        IFileSystem fileSystem)
    {
        if (!properties.TryGetValue("ActiveState", out var state) || string.IsNullOrWhiteSpace(state))
            return null;

        int? pid = null;
        if (properties.TryGetValue("MainPID", out var pidText)
            && int.TryParse(pidText.Trim(), out var parsed)
            && parsed > 0)
        {
            pid = parsed;
        }

        string? uptime = null;
        long? uptimeSeconds = null;
        if (properties.TryGetValue("ExecMainStartTimestamp", out var startText)
            && !string.IsNullOrWhiteSpace(startText)
            && TryParseSystemdTimestamp(startText, out var started))
        {
            var delta = DateTimeOffset.UtcNow - started;
            if (delta > TimeSpan.Zero)
            {
                uptime = FormatUptime(delta);
                uptimeSeconds = (long)delta.TotalSeconds;
            }
        }
        else if (pid is { } pidValue)
        {
            uptime = TryGetUptimeFromProc(pidValue, fileSystem);
            if (uptime is not null && TryParseUptimeToSeconds(uptime, out var secs))
                uptimeSeconds = secs;
        }

        return new InfoServiceStatus(state, pid, uptime, uptimeSeconds, null);
    }
}
