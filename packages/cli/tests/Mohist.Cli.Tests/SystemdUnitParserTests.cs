using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class SystemdUnitParserTests
{
    [Fact]
    public void ParseSystemdShow_ReturnsKeyValueMap()
    {
        var output = "ActiveState=active\nMainPID=1234\nFragmentPath=/etc/foo\n";

        var map = SystemdUnitParser.ParseSystemdShow(output);

        Assert.Equal("active", map["ActiveState"]);
        Assert.Equal("1234", map["MainPID"]);
        Assert.Equal("/etc/foo", map["FragmentPath"]);
    }

    [Fact]
    public void ParseSystemdShow_EmptyInput_ReturnsEmptyMap()
    {
        var map = SystemdUnitParser.ParseSystemdShow(string.Empty);

        Assert.Empty(map);
    }

    [Fact]
    public void ParseSystemdShow_SkipsLinesWithoutEquals()
    {
        var output = "ActiveState=active\nno-equals-line\nFragmentPath=/etc/foo\n";

        var map = SystemdUnitParser.ParseSystemdShow(output);

        Assert.Equal(2, map.Count);
        Assert.Equal("active", map["ActiveState"]);
        Assert.Equal("/etc/foo", map["FragmentPath"]);
    }

    [Fact]
    public void ParseSystemdValue_ReturnsValueForKnownKey()
    {
        var output = "ActiveState=active\nMainPID=42\n";

        Assert.Equal("active", SystemdUnitParser.ParseSystemdValue(output, "ActiveState"));
        Assert.Equal("42", SystemdUnitParser.ParseSystemdValue(output, "MainPID"));
    }

    [Fact]
    public void ParseSystemdValue_ReturnsNullForUnknownKey()
    {
        Assert.Null(SystemdUnitParser.ParseSystemdValue("ActiveState=active", "UnknownKey"));
    }

    [Fact]
    public void ParseSystemdUnit_HandlesCommentsAndTrailingWhitespace()
    {
        var content = "[Service]\n# This is a comment\nWorkingDirectory=/repo  \nExecStart=dotnet run  \n";

        var fields = SystemdUnitParser.ParseSystemdUnit(content);

        Assert.Equal("/repo", fields.WorkingDirectory);
        Assert.Equal("dotnet run", fields.ExecStart);
    }

    [Fact]
    public void ParseSystemdUnit_HandlesMissingKeys()
    {
        var content = "[Unit]\nDescription=Minimal\n";

        var fields = SystemdUnitParser.ParseSystemdUnit(content);

        Assert.Null(fields.WorkingDirectory);
        Assert.Null(fields.ExecStart);
    }

    [Fact]
    public void ParseSystemdUnit_OnlyPicksKnownKeys()
    {
        var content = "[Service]\nDescription=Some service\nWorkingDirectory=/repo\nExecStart=/bin/true\n";

        var fields = SystemdUnitParser.ParseSystemdUnit(content);

        Assert.Equal("/repo", fields.WorkingDirectory);
        Assert.Equal("/bin/true", fields.ExecStart);
    }

    [Fact]
    public void TryParseSystemdTimestamp_ParsesCommonFormat()
    {
        Assert.True(SystemdUnitParser.TryParseSystemdTimestamp("Mon 2026-01-01 10:00:00 UTC", out var ts));
        Assert.Equal(2026, ts.Year);
        Assert.Equal(1, ts.Month);
        Assert.Equal(1, ts.Day);
    }

    [Fact]
    public void TryParseSystemdTimestamp_ParsesIsoFormat()
    {
        Assert.True(SystemdUnitParser.TryParseSystemdTimestamp("2026-06-14 12:34:56", out var ts));
        Assert.Equal(2026, ts.Year);
        Assert.Equal(6, ts.Month);
        Assert.Equal(14, ts.Day);
        Assert.Equal(12, ts.Hour);
    }

    [Fact]
    public void TryParseSystemdTimestamp_ReturnsFalseOnEmpty()
    {
        Assert.False(SystemdUnitParser.TryParseSystemdTimestamp("", out _));
        Assert.False(SystemdUnitParser.TryParseSystemdTimestamp("   ", out _));
    }

    [Fact]
    public void TryParseUptimeToSeconds_ParsesCombinedFormat()
    {
        Assert.True(SystemdUnitParser.TryParseUptimeToSeconds("2d4h", out var secs));
        Assert.Equal(2 * 86_400 + 4 * 3_600, secs);
    }

    [Fact]
    public void TryParseUptimeToSeconds_ParsesHoursAndMinutes()
    {
        Assert.True(SystemdUnitParser.TryParseUptimeToSeconds("1h30m", out var secs));
        Assert.Equal(3_600 + 30 * 60, secs);
    }

    [Fact]
    public void TryParseUptimeToSeconds_ParsesSeconds()
    {
        Assert.True(SystemdUnitParser.TryParseUptimeToSeconds("45s", out var secs));
        Assert.Equal(45, secs);
    }

    [Fact]
    public void TryParseUptimeToSeconds_ReturnsFalseOnGarbage()
    {
        Assert.False(SystemdUnitParser.TryParseUptimeToSeconds("not-a-time", out _));
        Assert.False(SystemdUnitParser.TryParseUptimeToSeconds("", out _));
    }

    [Fact]
    public void FormatUptime_RendersHumanReadable()
    {
        Assert.Equal("0s", SystemdUnitParser.FormatUptime(TimeSpan.FromSeconds(0)));
        Assert.Equal("30s", SystemdUnitParser.FormatUptime(TimeSpan.FromSeconds(30)));
        Assert.Equal("5m", SystemdUnitParser.FormatUptime(TimeSpan.FromMinutes(5)));
        Assert.Equal("1h30m", SystemdUnitParser.FormatUptime(TimeSpan.FromMinutes(90)));
        Assert.Equal("2d4h", SystemdUnitParser.FormatUptime(TimeSpan.FromHours(52)));
    }

    [Fact]
    public void FormatUptime_ClampsNegativeToZero()
    {
        Assert.Equal("0s", SystemdUnitParser.FormatUptime(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void ParseStartTimeFromProcStat_ExtractsField22()
    {
        var stat = "1 (cat) R 0 1 1 0 -1 4194304 100 0 0 0 5 0 0 0 20 0 1 0 12345 1024 200 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 0 0";

        var startTime = SystemdUnitParser.ParseStartTimeFromProcStat(stat);

        Assert.NotNull(startTime);
        Assert.Equal(12345L, startTime.Value);
    }

    [Fact]
    public void ParseStartTimeFromProcStat_ReturnsNullOnMissingClosingParen()
    {
        Assert.Null(SystemdUnitParser.ParseStartTimeFromProcStat("no parens here"));
    }

    [Fact]
    public void BuildStatusFromProperties_ReturnsNullWhenActiveStateMissing()
    {
        var props = new Dictionary<string, string>();

        var status = SystemdUnitParser.BuildStatusFromProperties(props, new FakeFileSystem());

        Assert.Null(status);
    }

    [Fact]
    public void BuildStatusFromProperties_ParsesActiveStateAndPid()
    {
        var props = new Dictionary<string, string>
        {
            ["ActiveState"] = "active",
            ["MainPID"] = "1234",
            ["ExecMainStartTimestamp"] = "Mon 2026-01-01 10:00:00 UTC",
        };

        var status = SystemdUnitParser.BuildStatusFromProperties(props, new FakeFileSystem());

        Assert.NotNull(status);
        Assert.Equal("active", status!.State);
        Assert.Equal(1234, status.Pid);
        Assert.NotNull(status.Uptime);
    }

    [Fact]
    public void ParseSystemdEnvironment_ParsesEnvironmentLines()
    {
        var output = "Environment=FOO=bar\nEnvironment=BAZ=qux\n";

        var map = SystemdUnitParser.ParseSystemdEnvironment(output);

        Assert.Equal("bar", map["FOO"]);
        Assert.Equal("qux", map["BAZ"]);
    }

    [Fact]
    public void ParseSystemdEnvironment_IgnoresNonEnvironmentLines()
    {
        var output = "ActiveState=active\nEnvironment=FOO=bar\nDescription=test\n";

        var map = SystemdUnitParser.ParseSystemdEnvironment(output);

        Assert.Single(map);
        Assert.Equal("bar", map["FOO"]);
    }

    [Fact]
    public void TokenizeEnvironmentAssignments_HandlesQuotedValues()
    {
        var tokens = SystemdUnitParser.TokenizeEnvironmentAssignments("FOO=\"hello world\" BAR=baz").ToArray();

        Assert.Equal(new[] { "FOO=hello world", "BAR=baz" }, tokens);
    }

    [Fact]
    public void ReadRunnerIdSetting_OnlyUsesRunnerIdentityAssignments()
    {
        var setting = SystemdUnitParser.ReadRunnerIdSetting(
            "[Service]\nEnvironment=\"OTHER=value RUNNER_ID=not-an-assignment\" \"RUNNER_ID=runner-pluto\"\n");

        Assert.Equal("runner-pluto", setting.RunnerId);
        Assert.Null(setting.Error);
    }
}
