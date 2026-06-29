using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class InfoRenderer
{
    internal const string Unknown = "<unknown>";
    internal const string NotAGitRepo = "<not a git repo>";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    public void RenderDefault(TextWriter writer, InfoResult result)
    {
        writer.WriteLine(BuildCliLine(result.Cli));
        writer.WriteLine(BuildServiceLine("Server", result.Server, includeSource: true));
        writer.WriteLine(BuildSourceLine("  source", result.Server.Source));

        writer.WriteLine(BuildServiceLine("Runner", result.Runner, includeSource: true));
        writer.WriteLine(BuildSourceLine("  source", result.Runner.Source));

        writer.WriteLine(BuildProjectLine(result.Project));
        writer.WriteLine(BuildDataDirLine(result.DataDir));
        if (result.PlatformNotice is not null)
            writer.WriteLine(result.PlatformNotice);
    }

    public void RenderVerbose(TextWriter writer, InfoVerbose verbose)
    {
        writer.WriteLine();
        writer.WriteLine("Skills:");
        WriteIndentedList(writer, BuildSkillLines(verbose.Skills), indent: 2);

        writer.WriteLine("Git remote:");
        writer.WriteLine($"  origin: {BuildOriginUrl(verbose.GitRemote)}");

        writer.WriteLine("Opencode runtime:");
        writer.WriteLine($"  command: {verbose.OpencodeRuntime.Command ?? Unknown}");
        writer.WriteLine($"  version: {verbose.OpencodeRuntime.Version ?? Unknown}");
        writer.WriteLine($"  models:  {(verbose.OpencodeRuntime.ModelCount?.ToString() ?? Unknown)}");

        writer.WriteLine("Environment variables:");
        WriteIndentedList(writer, BuildEnvVarLines(verbose.EnvVars), indent: 2);

        writer.WriteLine("OS / Runtime:");
        writer.WriteLine($"  os:      {verbose.OsRuntime.Os ?? Unknown}");
        writer.WriteLine($"  arch:    {verbose.OsRuntime.Architecture ?? Unknown}");
        writer.WriteLine($"  dotnet:  {verbose.OsRuntime.DotnetVersion ?? Unknown}");
        writer.WriteLine($"  node:    {verbose.OsRuntime.NodeVersion ?? Unknown}");

        writer.WriteLine("Runner capacity:");
        writer.WriteLine($"  active:  {verbose.Capacity.ActiveWorkflows?.ToString() ?? Unknown}");

        writer.WriteLine("Disk usage breakdown:");
        WriteIndentedList(writer, BuildDiskCategoryLines(verbose.DiskUsage.Categories), indent: 2);
    }

    public void RenderJson(TextWriter writer, InfoResult result)
    {
        var root = BuildJsonObject(result);
        writer.WriteLine(root.ToJsonString(CompactJsonOptions));
    }

    internal static JsonObject BuildJsonObject(InfoResult result)
    {
        var root = new JsonObject
        {
            ["cli"] = BuildCliJson(result.Cli),
            ["server"] = BuildServiceJson(result.Server),
            ["runner"] = BuildServiceJson(result.Runner),
            ["project"] = result.Project is null ? null : BuildProjectJson(result.Project),
            ["dataDir"] = BuildDataDirJson(result.DataDir),
            ["platformNotice"] = result.PlatformNotice,
        };

        if (result.Verbose is { } verbose)
        {
            root["skills"] = BuildSkillsJson(verbose.Skills);
            root["gitRemote"] = BuildGitRemoteJson(verbose.GitRemote);
            root["opencodeRuntime"] = BuildOpencodeJson(verbose.OpencodeRuntime);
            root["envVars"] = BuildEnvVarsJson(verbose.EnvVars);
            root["osRuntime"] = BuildOsRuntimeJson(verbose.OsRuntime);
            root["capacity"] = BuildCapacityJson(verbose.Capacity);
            root["diskUsage"] = BuildDiskUsageJson(verbose.DiskUsage);
        }

        return root;
    }

    private static JsonObject BuildCliJson(InfoCli cli)
    {
        return new JsonObject
        {
            ["version"] = cli.Version ?? Unknown,
            ["binaryPath"] = cli.BinaryPath ?? Unknown,
            ["buildDate"] = cli.BuildDate ?? Unknown,
        };
    }

    private static JsonObject BuildServiceJson(InfoService service)
    {
        return new JsonObject
        {
            ["status"] = BuildServiceStatusJson(service.Status),
            ["source"] = BuildSourceJson(service.Source),
        };
    }

    private static JsonObject BuildServiceStatusJson(InfoServiceStatus? status)
    {
        if (status is null)
            return new JsonObject
            {
                ["state"] = Unknown,
                ["pid"] = null,
                ["uptime"] = Unknown,
                ["uptimeSeconds"] = null,
                ["connectivity"] = null,
            };
        return new JsonObject
        {
            ["state"] = status.State ?? Unknown,
            ["pid"] = NormalizePid(status.Pid),
            ["uptime"] = status.Uptime ?? Unknown,
            ["uptimeSeconds"] = status.UptimeSeconds,
            ["connectivity"] = status.Connectivity,
        };
    }

    private static int? NormalizePid(int? pid)
    {
        if (pid is null) return null;
        return pid.Value > 0 ? pid : null;
    }

    private static JsonObject? BuildSourceJson(InfoSource? source)
    {
        if (source is null)
            return null;
        string commitShort;
        string kind;
        switch (source.Kind)
        {
            case InfoSourceKind.Resolved:
                commitShort = source.CommitShort ?? Unknown;
                kind = "resolved";
                break;
            case InfoSourceKind.NotGitRepo:
                commitShort = NotAGitRepo;
                kind = "notGitRepo";
                break;
            default:
                commitShort = Unknown;
                kind = "unknown";
                break;
        }
        return new JsonObject
        {
            ["path"] = source.Path ?? Unknown,
            ["commitShort"] = commitShort,
            ["commitSubject"] = source.CommitSubject,
            ["kind"] = kind,
        };
    }

    private static JsonObject BuildProjectJson(InfoProject project)
    {
        return new JsonObject
        {
            ["id"] = project.Id,
            ["name"] = project.Name ?? Unknown,
            ["issueCount"] = project.IssueCount,
            ["activeIssueCount"] = project.ActiveIssueCount,
        };
    }

    private static JsonObject BuildDataDirJson(InfoDataDir dataDir)
    {
        return new JsonObject
        {
            ["path"] = dataDir.Path,
            ["size"] = dataDir.Size ?? Unknown,
        };
    }

    private static JsonArray BuildSkillsJson(InfoVerboseSkills skills)
    {
        var arr = new JsonArray();
        foreach (var skill in skills.Skills.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            arr.Add(new JsonObject
            {
                ["name"] = skill.Name,
                ["installPath"] = skill.InstallPath ?? Unknown,
            });
        }
        return arr;
    }

    private static JsonObject BuildGitRemoteJson(InfoVerboseGitRemote gitRemote)
    {
        return new JsonObject
        {
            ["originUrl"] = gitRemote.OriginUrl,
            ["isGitRepo"] = gitRemote.IsGitRepo,
        };
    }

    private static JsonObject BuildOpencodeJson(InfoVerboseOpencodeRuntime runtime)
    {
        return new JsonObject
        {
            ["command"] = runtime.Command ?? Unknown,
            ["version"] = runtime.Version ?? Unknown,
            ["modelCount"] = runtime.ModelCount,
            ["resolved"] = runtime.Resolved,
        };
    }

    private static JsonArray BuildEnvVarsJson(IReadOnlyList<InfoVerboseEnvVar> envVars)
    {
        var arr = new JsonArray();
        foreach (var env in envVars.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            arr.Add(new JsonObject
            {
                ["name"] = env.Name,
                ["value"] = env.Value ?? Unknown,
            });
        }
        return arr;
    }

    private static JsonObject BuildOsRuntimeJson(InfoVerboseOsRuntime osRuntime)
    {
        return new JsonObject
        {
            ["os"] = osRuntime.Os ?? Unknown,
            ["architecture"] = osRuntime.Architecture ?? Unknown,
            ["dotnetVersion"] = osRuntime.DotnetVersion ?? Unknown,
            ["nodeVersion"] = osRuntime.NodeVersion ?? Unknown,
        };
    }

    private static JsonObject BuildCapacityJson(InfoVerboseCapacity capacity)
    {
        return new JsonObject
        {
            ["activeWorkflows"] = capacity.ActiveWorkflows,
        };
    }

    private static JsonArray BuildDiskUsageJson(InfoVerboseDiskUsage diskUsage)
    {
        var arr = new JsonArray();
        foreach (var category in diskUsage.Categories.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            arr.Add(new JsonObject
            {
                ["name"] = category.Name,
                ["size"] = category.Size ?? Unknown,
                ["fileCount"] = category.FileCount,
            });
        }
        return arr;
    }

    private static void WriteIndentedList(TextWriter writer, IEnumerable<string> lines, int indent)
    {
        var prefix = new string(' ', indent);
        var hadAny = false;
        foreach (var line in lines)
        {
            writer.WriteLine(prefix + line);
            hadAny = true;
        }
        if (!hadAny)
            writer.WriteLine(prefix + $"<{Unknown}>");
    }

    internal static IEnumerable<string> BuildSkillLines(InfoVerboseSkills skills)
    {
        if (skills.Skills.Count == 0)
            return [];
        return skills.Skills
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => string.IsNullOrWhiteSpace(s.InstallPath)
                ? s.Name
                : $"{s.Name}  ({s.InstallPath})");
    }

    internal static string BuildOriginUrl(InfoVerboseGitRemote gitRemote)
    {
        if (gitRemote.OriginUrl is { } url)
            return url;
        return gitRemote.IsGitRepo ? NotAGitRepo : Unknown;
    }

    internal static IEnumerable<string> BuildEnvVarLines(IReadOnlyList<InfoVerboseEnvVar> envVars)
    {
        if (envVars.Count == 0)
            return [];
        return envVars
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Value is null ? $"{e.Name}=" : $"{e.Name}={e.Value}");
    }

    internal static IEnumerable<string> BuildDiskCategoryLines(IReadOnlyList<InfoVerboseDiskCategory> categories)
    {
        if (categories.Count == 0)
            return [];
        return categories
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c =>
            {
                var fileCountSuffix = c.FileCount is { } fc ? $"  ({fc} files)" : string.Empty;
                return c.Size is null
                    ? $"{c.Name}{fileCountSuffix}"
                    : $"{c.Name}  {c.Size}{fileCountSuffix}";
            });
    }

    internal static string BuildCliLine(InfoCli cli)
    {
        var version = cli.Version ?? Unknown;
        if (!string.IsNullOrEmpty(cli.Version) && !cli.Version.StartsWith('v'))
            version = "v" + version;
        var path = cli.BinaryPath ?? Unknown;
        if (!string.IsNullOrWhiteSpace(cli.BuildDate))
            return $"CLI          {version}  {path}  (built {cli.BuildDate})";
        return $"CLI          {version}  {path}";
    }

    internal static string BuildServiceLine(string label, InfoService service, bool includeSource)
    {
        var status = service.Status;
        if (status is null)
            return $"{label,-12}<unknown>";

        var state = status.State ?? SystemdUnitParser.NotRunning;
        var rest = string.Empty;
        if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
        {
            var pid = status.Pid is { } pidValue ? $"PID {pidValue}" : "<no pid>";
            var uptime = status.Uptime ?? "<no uptime>";
            rest = $"  {pid}  up {uptime}";
        }
        else if (string.Equals(state, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            state = SystemdUnitParser.NotRunning;
        }
        else if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
        {
            state = "failed";
        }
        else if (string.Equals(state, SystemdUnitParser.NotInstalled, StringComparison.OrdinalIgnoreCase))
        {
            state = SystemdUnitParser.NotInstalled;
        }
        var suffix = !string.IsNullOrEmpty(status.Connectivity) ? $" → {status.Connectivity}" : string.Empty;
        return $"{label,-12}{state}{rest}{suffix}";
    }

    internal static string BuildSourceLine(string prefix, InfoSource? source)
    {
        if (source is null)
            return $"{prefix,-12}{Unknown}  {Unknown}";

        var path = source.Path ?? Unknown;
        if (source.CommitShort is null)
            return $"{prefix,-12}{path}  {NotAGitRepo}";

        var subject = source.CommitSubject;
        var sha = source.CommitShort;
        if (string.IsNullOrWhiteSpace(subject))
            return $"{prefix,-12}{path}  @ {sha}";
        return $"{prefix,-12}{path}  @ {sha}  ({subject})";
    }

    internal static string BuildProjectLine(InfoProject? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Name))
            return "Project      <no project>";
        var total = project.IssueCount ?? 0;
        var active = project.ActiveIssueCount ?? 0;
        return $"Project      {project.Name}  ({total} issues, {active} active)";
    }

    internal static string BuildDataDirLine(InfoDataDir dir)
    {
        if (string.IsNullOrWhiteSpace(dir.Path))
            return "Data dir     <unknown>";
        if (string.IsNullOrWhiteSpace(dir.Size))
            return $"Data dir     {dir.Path}  ({Unknown})";
        return $"Data dir     {dir.Path}  ({dir.Size})";
    }
}
