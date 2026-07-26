using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderProjectList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No projects");
            return;
        }

        var hasBaseBranch = rows.Any(r => !string.IsNullOrEmpty(StringOf(r, "baseBranch")));

        if (!hasBaseBranch)
        {
            foreach (var row in rows)
            {
                var id = StringOf(row, "id");
                var name = StringOf(row, "name");
                var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                             string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                    ? "* "
                    : "  ";
                _out.WriteLine($"{marker}{name}");
            }
            return;
        }

        var headers = new[] { "*", "id", "name", "base branch" };
        var widths = new[] { 1, IdSoftCap, TitleSoftCap, 16 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "id");
            var name = StringOf(row, "name");
            var baseBranch = StringOf(row, "baseBranch");
            var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                         string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                ? "*"
                : "";
            cells.Add(new[] { marker, Truncate(id, IdSoftCap), Truncate(name, TitleSoftCap), Truncate(baseBranch, 16) });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderProjectShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var name = StringOf(data, "name");
        var baseBranch = StringOf(data, "baseBranch");
        var createdAt = StringOf(data, "createdAt");
        var updatedAt = StringOf(data, "updatedAt");
        var repos = data["repositories"] as JsonArray;
        var repoCount = repos?.Count.ToString() ?? "0";

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"base branch: {baseBranch}");
        _out.WriteLine($"repositories:{repoCount}");
        _out.WriteLine($"created:     {Truncate(createdAt, TitleSoftCap)}");
        _out.WriteLine($"updated:     {Truncate(updatedAt, TitleSoftCap)}");
    }

    private void RenderAgentList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "id", "name", "status", "updatedAt" };
        var widths = new[] { IdSoftCap, 24, 12, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            cells.Add(new[]
            {
                Truncate(StringOf(row, "id"), IdSoftCap),
                Truncate(StringOf(row, "name"), 24),
                Truncate(StringOf(row, "status"), 12),
                Truncate(StringOf(row, "updatedAt"), 24),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderAgentShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var skills = data["skills"] as JsonArray;
        var skillText = skills is null ? "" : string.Join(",", skills.Select(s => s?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

        _out.WriteLine($"id:                  {StringOf(data, "id")}");
        _out.WriteLine($"name:                {StringOf(data, "name")}");
        _out.WriteLine($"status:              {StringOf(data, "status")}");
        _out.WriteLine($"description:         {Truncate(StringOf(data, "description"), TitleSoftCap)}");
        _out.WriteLine($"max concurrent runs: {NumberOf(data, "maxConcurrentRuns")}");
        _out.WriteLine($"skills:              {skillText}");
        _out.WriteLine($"createdAt:           {Truncate(StringOf(data, "createdAt"), TitleSoftCap)}");
        _out.WriteLine($"updatedAt:           {Truncate(StringOf(data, "updatedAt"), TitleSoftCap)}");
        var instructions = StringOf(data, "instructions");
        if (!string.IsNullOrEmpty(instructions))
            _out.WriteLine($"instructions:        {Truncate(instructions, BodySoftCap)}");
    }

    private void RenderAgentSessionLaunch(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        _out.WriteLine($"job id:     {StringOf(data, "jobId")}");
        _out.WriteLine($"session id: {StringOf(data, "sessionId")}");
        _out.WriteLine($"agent id:   {StringOf(data, "agentId")}");
        _out.WriteLine($"agent name: {StringOf(data, "agentName")}");
        _out.WriteLine($"status:     {StringOf(data, "status")}");
    }

    private void RenderAgentSessionFollowup(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var status = StringOf(data, "status");
        var statusText = string.IsNullOrEmpty(status) ? "(no status returned)" : status;
        _out.WriteLine($"delivery: {statusText}");
    }

    private void RenderAgentSessionCancel(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var state = StringOf(data, "state");
        var stateText = string.IsNullOrEmpty(state) ? "(no state returned)" : state;
        _out.WriteLine($"state: {stateText}");
    }

    private void RenderAgentSessionList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "session id", "status", "created", "model" };
        var widths = new[] { IdSoftCap, 14, 24, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var sessionId = StringOf(row, "sessionId");
            var status = StringOf(row, "status");
            var createdAt = StringOf(row, "createdAt");
            var model = StringOf(row, "resolvedModel");
            cells.Add(new[]
            {
                Truncate(sessionId, IdSoftCap),
                Truncate(status, 14),
                Truncate(createdAt, 24),
                Truncate(model, TitleSoftCap),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderAgentSessionShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var agentId = StringOf(data, "agentId");
        var agentName = StringOf(data, "agentName");
        var status = StringOf(data, "status");
        var createdAt = StringOf(data, "createdAt");
        var lastActivityAt = StringOf(data, "lastActivityAt");
        var resolvedModel = StringOf(data, "resolvedModel");
        var failureReason = StringOf(data, "failureReason");
        var failureCategory = StringOf(data, "failureCategory");
        var toolCallCount = NumberOf(data, "toolCallCount");
        var toolErrorCount = NumberOf(data, "toolErrorCount");
        var contextRefs = data["contextRefs"] as JsonObject;

        var usage = data["usage"] as JsonObject;
        var totalTokens = NumberOf(usage, "totalTokens");
        var inputTokens = NumberOf(usage, "inputTokens");
        var outputTokens = NumberOf(usage, "outputTokens");
        var costAmount = NumberOf(usage, "costAmount");
        var costCurrency = StringOf(usage, "costCurrency");
        var costText = string.IsNullOrEmpty(costAmount) ? "" : $"{costAmount} {costCurrency}".Trim();
        var tokenText = FormatTokenUsage(inputTokens, outputTokens, totalTokens);

        _out.WriteLine($"agent:             {agentId} ({agentName})");
        _out.WriteLine($"status:            {status}");
        _out.WriteLine($"created:           {createdAt}");
        _out.WriteLine($"last active:       {lastActivityAt}");
        _out.WriteLine($"model:             {resolvedModel}");
        if (!string.IsNullOrEmpty(failureReason))
            _out.WriteLine($"failure reason:    {failureReason}");
        if (!string.IsNullOrEmpty(failureCategory))
            _out.WriteLine($"failure category:  {failureCategory}");
        _out.WriteLine($"tool calls:        {toolCallCount}");
        _out.WriteLine($"tool errors:       {toolErrorCount}");
        if (!string.IsNullOrEmpty(tokenText))
            _out.WriteLine($"tokens:            {tokenText}");
        if (!string.IsNullOrEmpty(costText))
            _out.WriteLine($"cost:              {costText}");
        if (contextRefs is not null)
        {
            var issueNumber = NumberOf(contextRefs, "issueNumber");
            var epicNumber = StringOf(contextRefs, "epicNumber");
            var repository = StringOf(contextRefs, "repository");
            var workspacePath = StringOf(contextRefs, "workspacePath");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(issueNumber))
                parts.Add($"issue #{issueNumber}");
            if (!string.IsNullOrEmpty(epicNumber))
                parts.Add($"epic #{epicNumber}");
            if (!string.IsNullOrEmpty(repository))
                parts.Add($"repo: {repository}");
            if (!string.IsNullOrEmpty(workspacePath))
                parts.Add($"ws: {workspacePath}");
            if (parts.Count > 0)
                _out.WriteLine($"context:           {string.Join(", ", parts)}");
        }
    }

    private void RenderAgentSessionTranscript(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var turns = data["turns"] as JsonArray ?? new JsonArray();
        var turnCount = turns.Count.ToString();
        var partCount = NumberOf(data, "partCount");
        var firstActivity = turns.Count > 0 ? StringOf(turns[0], "startedAt") : "";
        var lastActivity = StringOf(data, "lastActivityAt");

        _out.WriteLine($"turns:          {turnCount}");
        _out.WriteLine($"parts:          {partCount}");
        _out.WriteLine($"first activity: {firstActivity}");
        _out.WriteLine($"last activity:  {lastActivity}");
    }

    private void RenderAgentJobList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No agent jobs");
            return;
        }

        var headers = new[] { "job id", "status", "submitted", "terminal" };
        var widths = new[] { IdSoftCap, 12, 24, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            cells.Add(new[]
            {
                Truncate(StringOf(row, "jobId"), IdSoftCap),
                Truncate(StringOf(row, "status"), 12),
                Truncate(StringOf(row, "submittedAt"), 24),
                Truncate(StringOf(row, "terminalAt"), 24),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderAgentJobView(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        _out.WriteLine($"job id:          {StringOf(data, "jobId")}");
        _out.WriteLine($"status:          {StringOf(data, "status")}");
        var message = StringOf(data, "message");
        if (!string.IsNullOrEmpty(message))
            _out.WriteLine($"message:         {Truncate(message, BodySoftCap)}");
        var output = StringOf(data, "output");
        if (!string.IsNullOrEmpty(output))
            _out.WriteLine($"output:          {Truncate(output, BodySoftCap)}");
        if (data["artifactUploadIds"] is JsonArray artifacts && artifacts.Count > 0)
            _out.WriteLine($"artifacts:       {string.Join(",", artifacts.Select(a => a?.GetValue<string>() ?? ""))}");
        var failureReason = StringOf(data, "failureReason");
        if (!string.IsNullOrEmpty(failureReason))
            _out.WriteLine($"failure reason:  {failureReason}");
        var exitCode = NumberOf(data, "exitCode");
        if (!string.IsNullOrEmpty(exitCode))
            _out.WriteLine($"exit code:       {exitCode}");
    }

    private void RenderSessionList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No sessions");
            return;
        }

        var headers = new[] { "session id", "source", "owner", "last activity" };
        var widths = new[] { IdSoftCap, 14, TitleSoftCap, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            var id = StringOf(obj, "id");
            var source = StringOf(obj, "source");
            var owner = FormatSessionOwner(obj);
            var lastActivity = StringOf(obj, "lastActivityAt");
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(source, 14),
                Truncate(owner, TitleSoftCap),
                Truncate(lastActivity, 24),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderSessionShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var source = StringOf(data, "source");
        var activity = StringOf(data, "activity");
        var createdAt = StringOf(data, "createdAt");
        var lastActivityAt = StringOf(data, "lastActivityAt");
        var model = StringOf(data, "model") ?? StringOf(data, "resolvedModel");

        _out.WriteLine($"session id:     {id}");
        _out.WriteLine($"source:         {source}");
        _out.WriteLine($"activity:       {activity}");
        if (string.Equals(source, "agent-launch", StringComparison.Ordinal))
        {
            _out.WriteLine($"agent:          {StringOf(data, "agentId")} ({StringOf(data, "agentName")})");
        }
        else if (string.Equals(source, "workflow", StringComparison.Ordinal))
        {
            _out.WriteLine($"workflow run:   {StringOf(data, "workflowRunId")}");
            _out.WriteLine($"session name:   {StringOf(data, "sessionName")}");
        }
        _out.WriteLine($"created:        {createdAt}");
        _out.WriteLine($"last active:    {lastActivityAt}");
        if (!string.IsNullOrEmpty(model))
            _out.WriteLine($"model:          {model}");

        var contextRefs = data["contextRefs"] as JsonObject;
        if (contextRefs is not null)
        {
            var issueNumber = NumberOf(contextRefs, "issueNumber");
            var epicNumber = StringOf(contextRefs, "epicNumber");
            var repository = StringOf(contextRefs, "repository");
            var workspacePath = StringOf(contextRefs, "workspacePath");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(issueNumber))
                parts.Add($"issue #{issueNumber}");
            if (!string.IsNullOrEmpty(epicNumber))
                parts.Add($"epic #{epicNumber}");
            if (!string.IsNullOrEmpty(repository))
                parts.Add($"repo: {repository}");
            if (!string.IsNullOrEmpty(workspacePath))
                parts.Add($"ws: {workspacePath}");
            if (parts.Count > 0)
                _out.WriteLine($"context:        {string.Join(", ", parts)}");
        }

        var usage = data["usage"] as JsonObject;
        if (usage is not null)
        {
            var totalTokens = NumberOf(usage, "totalTokens");
            var inputTokens = NumberOf(usage, "inputTokens");
            var outputTokens = NumberOf(usage, "outputTokens");
            var costAmount = NumberOf(usage, "costAmount");
            var costCurrency = StringOf(usage, "costCurrency");
            var costText = string.IsNullOrEmpty(costAmount) ? "" : $"{costAmount} {costCurrency}".Trim();
            var tokenText = FormatTokenUsage(inputTokens, outputTokens, totalTokens);
            if (!string.IsNullOrEmpty(tokenText))
                _out.WriteLine($"tokens:         {tokenText}");
            if (!string.IsNullOrEmpty(costText))
                _out.WriteLine($"cost:           {costText}");
        }
    }

    private void RenderSessionTranscript(JsonNode? data)
    {
        var turns = data?["turns"] as JsonArray ?? new JsonArray();
        var partCount = data is null ? "" : NumberOf(data, "partCount");
        var firstActivity = turns.Count > 0 ? StringOf(turns[0], "startedAt") : "";
        var lastActivity = data is null ? "" : StringOf(data, "lastActivityAt");

        _out.WriteLine($"turns:          {turns.Count}");
        _out.WriteLine($"parts:          {partCount}");
        _out.WriteLine($"first activity: {firstActivity}");
        _out.WriteLine($"last activity:  {lastActivity}");
    }

    private void RenderSessionFollowup(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var status = StringOf(data, "status");
        var statusText = string.IsNullOrEmpty(status) ? "(no status returned)" : status;
        _out.WriteLine($"delivery: {statusText}");
    }

    private void RenderSessionCancel(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var state = StringOf(data, "state");
        var stateText = string.IsNullOrEmpty(state) ? "(no state returned)" : state;
        _out.WriteLine($"state: {stateText}");
    }

    private static string FormatSessionOwner(JsonObject obj)
    {
        var source = StringOf(obj, "source");
        if (string.Equals(source, "agent-launch", StringComparison.Ordinal))
        {
            var agentId = StringOf(obj, "agentId");
            var agentName = StringOf(obj, "agentName");
            if (!string.IsNullOrEmpty(agentName))
                return $"{agentName} ({agentId})";
            return agentId;
        }
        if (string.Equals(source, "workflow", StringComparison.Ordinal))
        {
            var run = StringOf(obj, "workflowRunId");
            var name = StringOf(obj, "sessionName");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(run))
                return $"{run}/{name}";
            return !string.IsNullOrEmpty(run) ? run : name;
        }
        return source;
    }

}
