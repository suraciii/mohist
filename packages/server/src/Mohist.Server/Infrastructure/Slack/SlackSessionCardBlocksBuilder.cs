using System.Text.Json;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackSessionCardBlocksBuilder : IScopedService
{
    private readonly SlackWebLinkBuilder _links;
    private readonly ProjectQuerier _projects;

    public SlackSessionCardBlocksBuilder(SlackWebLinkBuilder links, ProjectQuerier projects)
    {
        _links = links;
        _projects = projects;
    }

    public async Task<JsonElement?> BuildAsync(
        string projectId,
        string sessionId,
        JsonElement? controlBlocks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!_links.HasUsableExternalWebUrl)
            return controlBlocks;

        var project = await _projects.GetByIdAsync(projectId);
        var link = project is null
            ? null
            : _links.BuildOpenSession(project.Name, sessionId);
        return Combine(controlBlocks, link?.Blocks);
    }

    private static JsonElement? Combine(JsonElement? first, JsonElement? second)
    {
        var blocks = new List<JsonElement>();
        Add(blocks, first);
        Add(blocks, second);
        return blocks.Count == 0 ? null : JsonSerializer.SerializeToElement(blocks);
    }

    private static void Add(List<JsonElement> target, JsonElement? source)
    {
        if (source is { ValueKind: JsonValueKind.Array })
            target.AddRange(source.Value.EnumerateArray().Select(block => block.Clone()));
    }
}
