using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    public async Task<int> PrintOpencodeModelsAsync(string projectId, string? runtime, string mode)
    {
        var localExit = HandleLocalJsonSelection(mode, nameof(TableShape.Models));
        if (localExit is not null)
            return localExit.Value;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _err.WriteLine(MohistCliCommands.NoActiveProjectMessage);
            return 1;
        }

        JsonNode? data;
        try
        {
            var runtimeQuery = string.IsNullOrWhiteSpace(runtime)
                ? string.Empty
                : $"?runtime={Uri.EscapeDataString(runtime.Trim())}";
            data = await GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/opencode/models{runtimeQuery}");
        }
        catch (HttpRequestException)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }
        catch (ApiResponseException ex)
        {
            WriteApiFailure(ex);
            return FailureExitCode(ex.StatusCode);
        }

        if (data is null)
        {
            _err.WriteLine(ServerUnavailableMessage);
            return 1;
        }

        var models = data["models"] as JsonArray ?? new JsonArray();
        if (mode.StartsWith("json:", StringComparison.Ordinal))
        {
            var resources = new JsonArray();
            foreach (var item in models)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var id))
                    resources.Add(new JsonObject { ["id"] = id });
            }
            return await WriteSelectedDataAsync(resources, mode, nameof(TableShape.Models));
        }

        foreach (var item in models)
        {
            var id = item?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                _out.WriteLine(id);
        }
        return 0;
    }
}
