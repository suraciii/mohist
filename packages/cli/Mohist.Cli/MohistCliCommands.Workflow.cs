using System.CommandLine;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class WorkflowCommands
{
    internal const string ServerNotRunningMessage =
        "Server is not running. Start with: mo server start";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Manage workflow profiles");
        workflow.Subcommands.Add(BuildList(api));
        return workflow;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var list = new Command("list", "List available workflow profiles");
        list.Aliases.Add("ls");
        var jsonOption = new Option<bool>("--json") { Description = "Output JSON" };
        list.Options.Add(jsonOption);
        list.SetAction(async ctx =>
        {
            var json = ctx.GetValue(jsonOption);
            return await ListAsync(api, json);
        });
        return list;
    }

    private static async Task<int> ListAsync(MohistCliApi api, bool json)
    {
        JsonNode? data;
        try
        {
            data = await api.GetDataAsync("/api/workflow-templates/system");
        }
        catch (HttpRequestException)
        {
            return ReportServerNotRunning(api);
        }
        catch (TaskCanceledException)
        {
            return ReportServerNotRunning(api);
        }
        catch (SocketException)
        {
            return ReportServerNotRunning(api);
        }

        var profiles = data as JsonArray;
        if (profiles is null)
        {
            api.Error.WriteLine("Server returned an unexpected response for workflow profiles.");
            return 1;
        }

        if (json)
        {
            var projected = ProjectProfiles(profiles);
            await api.Output.WriteLineAsync(JsonSerializer.Serialize(projected, JsonOptions));
            return 0;
        }

        await WriteHumanAsync(api.Output, profiles);
        return 0;
    }

    private static List<object> ProjectProfiles(JsonArray profiles)
    {
        var result = new List<object>(profiles.Count);
        foreach (var node in profiles)
        {
            if (node is not JsonObject obj)
                continue;

            var id = obj["id"]?.GetValue<string>() ?? string.Empty;
            var name = obj["name"]?.GetValue<string>() ?? string.Empty;
            var description = obj["description"]?.GetValue<string>() ?? string.Empty;
            var isDefault = obj["isDefault"]?.GetValue<bool>() ?? false;

            result.Add(new
            {
                id,
                displayName = name,
                description,
                isDefault,
            });
        }
        return result;
    }

    private static async Task WriteHumanAsync(TextWriter output, JsonArray profiles)
    {
        var first = true;
        foreach (var node in profiles)
        {
            if (node is not JsonObject obj)
                continue;

            if (!first)
                await output.WriteLineAsync();

            var id = obj["id"]?.GetValue<string>() ?? string.Empty;
            var name = obj["name"]?.GetValue<string>() ?? id;
            var description = obj["description"]?.GetValue<string>() ?? string.Empty;
            var isDefault = obj["isDefault"]?.GetValue<bool>() ?? false;

            var indicator = isDefault ? " (default)" : string.Empty;
            await output.WriteLineAsync($"{name}{indicator}  [{id}]");
            if (!string.IsNullOrEmpty(description))
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync(description);
            }

            first = false;
        }
    }

    private static int ReportServerNotRunning(MohistCliApi api)
    {
        api.Error.WriteLine(ServerNotRunningMessage);
        return 1;
    }
}
