using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("Mohist.Server.Tests")]

internal static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        var cli = new MohistCli(args, Console.Out, Console.Error);
        return await cli.RunAsync();
    }
}

internal sealed class MohistCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string[] _args;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly HttpClient _http;
    private readonly SystemdServiceInstaller _systemd;

    public MohistCli(string[] args, TextWriter output, TextWriter error)
    {
        _args = args;
        _out = output;
        _err = error;
        _http = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://localhost:3456"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _systemd = new SystemdServiceInstaller(output, error);
    }

    public MohistCli(string[] args, TextWriter output, TextWriter error, HttpClient http, SystemdServiceInstaller? systemd = null)
    {
        _args = args;
        _out = output;
        _err = error;
        _http = http;
        _systemd = systemd ?? new SystemdServiceInstaller(output, error);
    }

    public async Task<int> RunAsync()
    {
        if (_args.Length == 0 || IsHelp(_args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return _args[0] switch
            {
                "server" => await ServerAsync(_args[1..]),
                "runner" => await RunnerAsync(_args[1..]),
                "status" => await PrintGetAsync("/api/status" + Query(All: true)),
                "project" => await ProjectAsync(_args[1..]),
                "issue" => await IssueAsync(_args[1..]),
                "config" => await ConfigAsync(_args[1..]),
                "providers" or "provider" => await ProvidersAsync(_args[1..]),
                "logs" => await PrintGetAsync("/api/logs/tail"),
                _ => UsageError($"Unknown command '{_args[0]}'"),
            };
        }
        catch (HttpRequestException ex)
        {
            _err.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (TaskCanceledException)
        {
            _err.WriteLine("Request timed out");
            return 1;
        }
        catch (ArgumentException ex)
        {
            _err.WriteLine(ex.Message);
            return 2;
        }
    }

    private async Task<int> ServerAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo server <status|health|install|start|stop|restart|logs|uninstall>");
            return 0;
        }

        return args[0] switch
        {
            "health" => await PrintGetAsync("/api/health"),
            "install" => await _systemd.InstallServerAsync(ServiceInstallOptions.From(args[1..])),
            "status" => await _systemd.StatusServerAsync(ServiceCommandOptions.From(args[1..])),
            "start" => await _systemd.StartServerAsync(ServiceCommandOptions.From(args[1..])),
            "stop" => await _systemd.StopServerAsync(ServiceCommandOptions.From(args[1..])),
            "restart" => await _systemd.RestartServerAsync(ServiceCommandOptions.From(args[1..])),
            "logs" => await _systemd.LogsServerAsync(ServiceCommandOptions.From(args[1..])),
            "uninstall" or "remove" => await _systemd.UninstallServerAsync(ServiceCommandOptions.From(args[1..])),
            _ => UsageError($"Unknown server command '{args[0]}'"),
        };
    }

    private async Task<int> RunnerAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo runner <install|status|start|stop|restart|logs|uninstall>");
            return 0;
        }

        return args[0] switch
        {
            "install" => await _systemd.InstallRunnerAsync(ServiceInstallOptions.From(args[1..])),
            "status" => await _systemd.StatusRunnerAsync(ServiceCommandOptions.From(args[1..])),
            "start" => await _systemd.StartRunnerAsync(ServiceCommandOptions.From(args[1..])),
            "stop" => await _systemd.StopRunnerAsync(ServiceCommandOptions.From(args[1..])),
            "restart" => await _systemd.RestartRunnerAsync(ServiceCommandOptions.From(args[1..])),
            "logs" => await _systemd.LogsRunnerAsync(ServiceCommandOptions.From(args[1..])),
            "uninstall" or "remove" => await _systemd.UninstallRunnerAsync(ServiceCommandOptions.From(args[1..])),
            _ => UsageError($"Unknown runner command '{args[0]}'"),
        };
    }

    private async Task<int> ProjectAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo project <list|create|show|use|delete> ...");
            return 0;
        }

        return args[0] switch
        {
            "list" or "ls" => await PrintGetAsync("/api/projects"),
            "show" => await PrintGetAsync($"/api/projects/{Escape(Required(args, 1, "project name"))}"),
            "use" => await PrintPostAsync($"/api/projects/{Escape(Required(args, 1, "project name"))}/use", new { }),
            "delete" or "remove" or "rm" => await PrintDeleteAsync($"/api/projects/{Escape(Required(args, 1, "project name"))}"),
            "create" => await CreateProjectAsync(args[1..]),
            _ => UsageError($"Unknown project command '{args[0]}'"),
        };
    }

    private async Task<int> CreateProjectAsync(string[] args)
    {
        var name = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))
            ?? throw new ArgumentException("project create requires a name");
        var path = Option(args, "--path", "-p") ?? Environment.CurrentDirectory;
        var baseBranch = Option(args, "--base-branch", "-b");
        return await PrintPostAsync("/api/projects", new { name, path, baseBranch });
    }

    private async Task<int> IssueAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo issue <list|create|show|update|start|approve|reject|close|reopen|retry|rerun|rebase|archive|unarchive|logs|diff|sessions|workflow> ...");
            return 0;
        }

        return args[0] switch
        {
            "list" or "ls" => await ListIssuesAsync(args[1..]),
            "create" => await CreateIssueAsync(args[1..]),
            "show" => await PrintGetAsync(IssuePath(args, 1)),
            "update" => await UpdateIssueAsync(args[1..]),
            "start" => await IssuePostAsync(args, "start"),
            "approve" => await IssuePostAsync(args, "approve"),
            "reject" => await RejectIssueAsync(args[1..]),
            "close" => await IssuePostAsync(args, "close"),
            "reopen" => await IssuePostAsync(args, "reopen"),
            "retry" => await IssuePostAsync(args, "retry"),
            "rerun" => await IssuePostAsync(args, "rerun"),
            "stop" or "force-stop" => await IssuePostAsync(args, "force-stop"),
            "resume" => await IssuePostAsync(args, "resume"),
            "rebase" => await RebaseIssueAsync(args[1..]),
            "archive" => await ArchiveIssueAsync(args[1..]),
            "unarchive" => await IssuePostAsync(args, "unarchive"),
            "logs" => await PrintGetAsync(IssuePath(args, 1, "logs")),
            "events" => await PrintGetAsync(IssuePath(args, 1, "events")),
            "diff" => await PrintGetAsync(IssuePath(args, 1, "diff")),
            "commits" => await PrintGetAsync(IssuePath(args, 1, "commits")),
            "sessions" or "coder-sessions" => await PrintGetAsync(IssuePath(args, 1, "coder-sessions")),
            "workflow" => await IssueWorkflowAsync(args[1..]),
            _ => UsageError($"Unknown issue command '{args[0]}'"),
        };
    }

    private async Task<int> ListIssuesAsync(string[] args)
    {
        var query = Query(
            ProjectId: Option(args, "--project-id"),
            Stage: Option(args, "--stage", "-s"),
            Label: Option(args, "--label", "-l"),
            Priority: Option(args, "--priority", "-p"),
            Archived: HasFlag(args, "--archived") ? true : null,
            All: HasFlag(args, "--all") ? true : null);
        return await PrintGetAsync("/api/issues" + query);
    }

    private async Task<int> CreateIssueAsync(string[] args)
    {
        var title = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))
            ?? throw new ArgumentException("issue create requires a title");
        var labels = Values(args, "--label", "-l");
        var priority = Option(args, "--priority", "-p") ?? "p2";
        var body = Option(args, "--body", "-b") ?? "";
        var projectId = Option(args, "--project-id");
        var model = Option(args, "--model");
        var workflowProfileId = Option(args, "--workflow-profile");

        return await PrintPostAsync("/api/issues", new
        {
            title,
            body,
            labels,
            priority,
            projectId,
            model,
            workflowProfileId,
        });
    }

    private async Task<int> UpdateIssueAsync(string[] args)
    {
        var number = Required(args, 0, "issue number");
        var query = ProjectQuery(args);
        return await PrintPatchAsync($"/api/issues/{Escape(number)}{query}", new
        {
            title = Option(args, "--title"),
            body = Option(args, "--body", "-b"),
            labels = Values(args, "--label", "-l"),
            priority = Option(args, "--priority", "-p"),
            model = Option(args, "--model"),
        });
    }

    private async Task<int> RejectIssueAsync(string[] args)
    {
        var number = Required(args, 0, "issue number");
        var query = ProjectQuery(args);
        var reason = Option(args, "--reason", "-m");
        return await PrintPostAsync($"/api/issues/{Escape(number)}/reject{query}", new { reason });
    }

    private async Task<int> RebaseIssueAsync(string[] args)
    {
        var number = Required(args, 0, "issue number");
        var query = ProjectQuery(args);
        var baseBranch = Option(args, "--base-branch", "-b");
        return await PrintPostAsync($"/api/issues/{Escape(number)}/rebase{query}", new { baseBranch });
    }

    private async Task<int> ArchiveIssueAsync(string[] args)
    {
        if (HasFlag(args, "--all-completed"))
            return await PrintPostAsync("/api/issues/archive-completed" + ProjectQuery(args), new { });
        return await IssuePostAsync(["archive", Required(args, 0, "issue number"), .. args[1..]], "archive");
    }

    private async Task<int> IssueWorkflowAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo issue workflow <status|timeline> <number>");
            return 0;
        }

        var subresource = args[0] switch
        {
            "status" => "workflow/status",
            "timeline" => "workflow/timeline",
            _ => throw new ArgumentException($"Unknown issue workflow command '{args[0]}'"),
        };
        return await PrintGetAsync(IssuePath(args, 1, subresource));
    }

    private async Task<int> IssuePostAsync(string[] args, string action)
    {
        var number = Required(args, 1, "issue number");
        return await PrintPostAsync($"/api/issues/{Escape(number)}/{action}{ProjectQuery(args)}", new { });
    }

    private async Task<int> ConfigAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo config <list|get|set> ...");
            return 0;
        }

        return args[0] switch
        {
            "list" => await PrintGetAsync("/api/config/list"),
            "get" => await PrintGetAsync($"/api/config/{Escape(Required(args, 1, "key"))}"),
            "set" => await PrintPutAsync($"/api/config/{Escape(Required(args, 1, "key"))}", new { value = Required(args, 2, "value") }),
            _ => UsageError($"Unknown config command '{args[0]}'"),
        };
    }

    private async Task<int> ProvidersAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            _out.WriteLine("Usage: mo providers <list|models|runtime|save|test|delete> ...");
            return 0;
        }

        return args[0] switch
        {
            "list" or "ls" => await PrintGetAsync("/api/providers"),
            "models" => await PrintGetAsync("/api/providers/models"),
            "runtime" => await PrintGetAsync("/api/providers/runtime"),
            "save" => await SaveProviderAsync(args[1..]),
            "test" => await TestProviderAsync(args[1..]),
            "delete" or "remove" or "rm" => await PrintDeleteAsync($"/api/providers/{Escape(Required(args, 1, "provider id"))}"),
            _ => UsageError($"Unknown providers command '{args[0]}'"),
        };
    }

    private async Task<int> SaveProviderAsync(string[] args)
    {
        var id = Required(args, 0, "provider id");
        var apiKey = Option(args, "--api-key", "--key")
            ?? throw new ArgumentException("providers save requires --api-key");
        return await PrintPostAsync($"/api/providers/{Escape(id)}", new
        {
            name = Option(args, "--name"),
            apiKey,
            baseURL = Option(args, "--base-url", "--baseURL"),
            models = Values(args, "--model"),
            sdk = Option(args, "--sdk"),
        });
    }

    private async Task<int> TestProviderAsync(string[] args)
    {
        var apiKey = Option(args, "--api-key", "--key")
            ?? throw new ArgumentException("providers test requires --api-key");
        return await PrintPostAsync("/api/providers/test", new
        {
            name = Option(args, "--name"),
            apiKey,
            baseURL = Option(args, "--base-url", "--baseURL"),
            models = Values(args, "--model"),
            sdk = Option(args, "--sdk"),
        });
    }

    private async Task<int> PrintGetAsync(string path) => await PrintResponseAsync(await _http.GetAsync(path));

    private async Task<int> PrintDeleteAsync(string path) => await PrintResponseAsync(await _http.DeleteAsync(path));

    private async Task<int> PrintPostAsync(string path, object body) => await PrintResponseAsync(await _http.PostAsJsonAsync(path, body, JsonOptions));

    private async Task<int> PrintPutAsync(string path, object body) => await PrintResponseAsync(await _http.PutAsJsonAsync(path, body, JsonOptions));

    private async Task<int> PrintPatchAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await PrintResponseAsync(await _http.SendAsync(request));
    }

    private async Task<int> PrintResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);
        if (node is null)
        {
            _out.WriteLine(response.StatusCode);
            return response.IsSuccessStatusCode ? 0 : 1;
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (success)
        {
            var data = node["data"];
            _out.WriteLine(data is null ? "OK" : data.ToJsonString(JsonOptions));
            return 0;
        }

        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        _err.WriteLine(code is null ? error : $"{error} ({code})");
        return response.StatusCode == HttpStatusCode.NotFound ? 4 : 1;
    }

    private static string IssuePath(string[] args, int numberIndex, string? subresource = null)
    {
        var number = Required(args, numberIndex, "issue number");
        var path = $"/api/issues/{Escape(number)}";
        if (!string.IsNullOrWhiteSpace(subresource)) path += "/" + subresource;
        return path + ProjectQuery(args);
    }

    private static string ProjectQuery(string[] args) => Query(ProjectId: Option(args, "--project-id"));

    private static string Query(
        string? ProjectId = null,
        string? Stage = null,
        string? Label = null,
        string? Priority = null,
        bool? Archived = null,
        bool? All = null)
    {
        var parts = new List<string>();
        Add("projectId", ProjectId);
        Add("stage", Stage);
        Add("label", Label);
        Add("priority", Priority);
        Add("archived", Archived?.ToString().ToLowerInvariant());
        Add("all", All?.ToString().ToLowerInvariant());
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Required(string[] args, int index, string name)
    {
        if (args.Length <= index || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException($"{name} is required");
        return args[index];
    }

    private static string? Option(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!names.Contains(args[i])) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value");
            return args[i + 1];
        }
        return null;
    }

    private static string[] Values(string[] args, params string[] names)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!names.Contains(args[i])) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value");
            values.Add(args[i + 1]);
        }
        return values.ToArray();
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name);

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private int UsageError(string message)
    {
        _err.WriteLine(message);
        _err.WriteLine("Run 'mo --help' for usage.");
        return 2;
    }

    private void PrintHelp()
    {
        _out.WriteLine("""
        Mohist CLI

        Environment:
          MOHIST_SERVER_URL   Server URL, default http://localhost:3456

        Commands:
          mo server status
          mo server health
          mo server install [--repo-root <path>] [--unit-dir <path>] [--listen-url <url>] [--dry-run]
          mo server start|stop|restart|status|uninstall [--dry-run]
          mo server logs [-n <lines>] [--follow] [--dry-run]
          mo runner install [--repo-root <path>] [--unit-dir <path>] [--server-url <url>] [--runner-root <path>] [--dry-run]
          mo runner start|stop|restart|status|uninstall [--dry-run]
          mo runner logs [-n <lines>] [--follow] [--dry-run]
          mo status
          mo project list
          mo project create <name> [--path <path>] [--base-branch <branch>]
          mo project show <name>
          mo project use <name>
          mo project delete <name>
          mo issue list [--all] [--stage <stage>] [--label <label>] [--priority <p1>]
          mo issue create <title> [--body <body>] [--label <label>] [--priority <p1>] [--project-id <id>]
          mo issue show <number> [--project-id <id>]
          mo issue update <number> [--title <title>] [--body <body>] [--label <label>] [--priority <p1>]
          mo issue start|approve|reject|close|reopen|retry|rerun|resume|rebase <number>
          mo issue archive <number>
          mo issue archive --all-completed
          mo issue unarchive <number>
          mo issue logs|events|diff|commits|sessions <number>
          mo issue workflow status|timeline <number>
          mo config list|get|set
          mo providers list|models|runtime|save|test|delete
        """);
    }
}
