using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;

namespace Mohist.Server.Sessions.Services;

internal static class SessionTranscriptBuilder
{
    public static AgentSessionTranscriptResponse Build(AgentSessionTranscriptData transcript)
    {
        var responseTurns = new List<AgentSessionTranscriptTurnDto>();
        var partsByTurn = transcript.Parts
            .GroupBy(p => p.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Sequence).ThenBy(p => p.Id).ToList());
        var toolPartIndex = new Dictionary<string, AgentSessionTranscriptPartDto>(StringComparer.Ordinal);

        foreach (var turn in transcript.Turns)
        {
            var at = turn.StartedAt.ToString("o");
            var dto = new AgentSessionTranscriptTurnDto
            {
                Id = $"turn-{turn.Sequence}",
                StartedAt = at,
                CompletedAt = null,
                Incomplete = false,
                User = new AgentSessionTranscriptUserDto
                {
                    Text = turn.PromptText,
                    Kind = AgentSessionJsonHelper.NormalizePromptKind(turn.PromptKind),
                    SentAt = at,
                    RuntimeSessionId = turn.RuntimeSessionId,
                },
            };
            toolPartIndex.Clear();

            if (partsByTurn.TryGetValue(turn.Id, out var parts))
            {
                var partIndex = 0;
                foreach (var part in parts)
                {
                    var payload = AgentSessionJsonHelper.ParsePayloadOrEmpty(part.PayloadJson);
                    var partAt = part.FirstSeenAt.ToString("o");
                    if (part.Type == "text" || part.Type == "reasoning")
                    {
                        dto.Assistant.Add(new AgentSessionTranscriptPartDto
                        {
                            Id = $"{dto.Id}-p{++partIndex}",
                            Type = part.Type,
                            Text = part.Text,
                            StartedAt = partAt,
                            CompletedAt = null,
                        });
                        continue;
                    }

                    if (part.Type == "tool")
                    {
                        UpsertToolPart(dto, toolPartIndex, part, payload, partAt, ref partIndex);
                        continue;
                    }

                    if (part.Type == "status")
                    {
                        if (AgentSessionJsonHelper.GetStringProp(payload, "status") == "failed")
                        {
                            dto.Assistant.Add(new AgentSessionTranscriptPartDto
                            {
                                Id = $"{dto.Id}-p{++partIndex}",
                                Type = "error",
                                Message = AgentSessionJsonHelper.GetStringProp(payload, "failureReason") ?? "Liveness failed",
                                Kind = "recovery",
                                At = partAt,
                            });
                        }
                        continue;
                    }

                    if (part.Type == TranscriptPartTypes.SessionClosed)
                    {
                        var status = AgentSessionJsonHelper.GetStringProp(payload, "status") ?? "completed";
                        if (status is "failed" or "cancelled")
                        {
                            dto.Assistant.Add(new AgentSessionTranscriptPartDto
                            {
                                Id = $"{dto.Id}-p{++partIndex}",
                                Type = "error",
                                Message = AgentSessionJsonHelper.GetStringProp(payload, "failureReason") ?? $"Session {status}",
                                Kind = status == "cancelled" ? "cancelled" : "failed",
                                At = partAt,
                            });
                        }
                    }
                }
            }

            responseTurns.Add(dto);
        }

        var lastActivityAt = transcript.Parts.Count > 0
            ? transcript.Parts.Max(p => p.LastSeenAt).ToString("o")
            : transcript.Turns.LastOrDefault()?.UpdatedAt.ToString("o");

        return new AgentSessionTranscriptResponse
        {
            Turns = responseTurns,
            PartCount = transcript.Parts.Count,
            LastActivityAt = lastActivityAt,
        };
    }

    private static void UpsertToolPart(
        AgentSessionTranscriptTurnDto turn,
        IDictionary<string, AgentSessionTranscriptPartDto> toolPartIndex,
        AgentSessionTranscriptPartRow partRow,
        JsonElement payload,
        string at,
        ref int partIndex)
    {
        var toolCallId = AgentSessionJsonHelper.GetToolStringProp(payload, "toolCallId")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "id")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "callId");
        if (string.IsNullOrWhiteSpace(toolCallId))
            return;

        var status = MapToolStatus(AgentSessionJsonHelper.GetToolStringProp(payload, "status") ?? AgentSessionJsonHelper.GetToolStringProp(payload, "state"));
        var rawInput = GetToolRaw(payload, "rawInput") ?? GetToolRaw(payload, "input");
        var rawOutput = GetToolRaw(payload, "rawOutput") ?? GetToolRaw(payload, "output");

        if (toolPartIndex.TryGetValue(toolCallId, out var existing) && existing.Tool is not null)
        {
            existing.Tool.Status = status;
            existing.Tool.Title = AgentSessionJsonHelper.GetToolStringProp(payload, "title") ?? existing.Tool.Title;
            existing.Tool.Input = rawInput ?? existing.Tool.Input;
            existing.Tool.Output = rawOutput ?? existing.Tool.Output;
            existing.Tool.RawInput = rawInput ?? existing.Tool.RawInput;
            existing.Tool.RawOutput = rawOutput ?? existing.Tool.RawOutput;
            existing.Tool.Error = status == "failed" ? rawOutput : existing.Tool.Error;
            if (status is "completed" or "failed" or "cancelled")
            {
                existing.Tool.CompletedAt = at;
                existing.CompletedAt = at;
            }
            return;
        }

        var toolName = AgentSessionJsonHelper.GetToolStringProp(payload, "toolName")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "kind")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "name")
            ?? "unknown";
        var title = AgentSessionJsonHelper.GetToolStringProp(payload, "title");
        var completedAt = status is "completed" or "failed" or "cancelled" ? at : null;
        var part = new AgentSessionTranscriptPartDto
        {
            Id = $"{turn.Id}-p{++partIndex}",
            Type = "tool",
            StartedAt = at,
            CompletedAt = completedAt,
            Tool = new AgentSessionTranscriptToolDto
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                NormalizedName = NormalizeToolName(toolName, title),
                Status = status,
                Title = title,
                Input = rawInput,
                Output = rawOutput,
                RawInput = rawInput,
                RawOutput = rawOutput,
                Error = status == "failed" ? rawOutput : null,
                StartedAt = at,
                CompletedAt = completedAt,
            },
        };
        toolPartIndex[toolCallId] = part;
        turn.Assistant.Add(part);
    }

    private static string MapToolStatus(string? status) => status switch
    {
        "completed" => "completed",
        "failed" or "timeout" => "failed",
        "cancelled" => "cancelled",
        "running" or "in_progress" or "started" => "running",
        _ => "pending"
    };

    private static string NormalizeToolName(string toolName, string? title)
    {
        var value = !string.IsNullOrWhiteSpace(toolName) ? toolName : title;
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return value.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private static string? GetToolRaw(JsonElement payload, string name)
    {
        if (TryGetRaw(payload, name, out var raw)) return raw;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall)
            && TryGetRaw(toolCall, name, out raw))
            return raw;
        return null;
    }

    private static bool TryGetRaw(JsonElement payload, string name, out string? raw)
    {
        raw = null;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var prop))
            return false;
        raw = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
        return true;
    }
}
