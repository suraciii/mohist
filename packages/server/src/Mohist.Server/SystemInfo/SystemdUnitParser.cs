namespace Mohist.Server.SystemInfo;

public sealed record SystemdUnitParseResult(
    string? WorkingDirectory,
    string? ExecStart,
    string? Description);

public static class SystemdUnitParser
{
    public static SystemdUnitParseResult Parse(string content)
    {
        string? workingDirectory = null;
        string? execStart = null;
        string? description = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('['))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "WorkingDirectory":
                    workingDirectory = value;
                    break;
                case "ExecStart":
                    execStart = value;
                    break;
                case "Description":
                    description = value;
                    break;
            }
        }

        return new SystemdUnitParseResult(workingDirectory, execStart, description);
    }
}
