using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class SessionTranscriptBuilder
{
    public static AgentSessionTranscriptResponse Build(
        AgentSessionTranscriptData transcript,
        AgentSession? session = null,
        string? view = null)
    {
        var diagnostic = IsDiagnosticView(view);
        var responseTurns = new List<AgentSessionTranscriptTurnDto>();
        var partsByTurn = transcript.Parts
            .GroupBy(p => p.TurnId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Sequence).ThenBy(p => p.Id).ToList());
        var turns = transcript.Turns
            .Concat(MissingCanonicalTurns(transcript.Turns, session))
            .OrderBy(turn => turn.Sequence)
            .ThenBy(turn => turn.Id)
            .ToList();
        var toolPartIndex = new Dictionary<string, AgentSessionTranscriptPartDto>(StringComparer.Ordinal);

        foreach (var turn in turns)
        {
            var at = turn.StartedAt.ToString("o");
            partsByTurn.TryGetValue(turn.Id, out var parts);
            var recoveryPrompt = parts is null ? null : RecoveryPromptText(parts);
            var canonicalTurn = session?.Status.Turns?.FirstOrDefault(candidate => candidate.Sequence == turn.Sequence);
            var status = ResolveTurnStatus(canonicalTurn, parts, session?.Status.Activity);
            var dto = new AgentSessionTranscriptTurnDto
            {
                Id = canonicalTurn?.Id ?? turn.Id.ToString(CultureInfo.InvariantCulture),
                StartedAt = at,
                CompletedAt = null,
                Incomplete = status is "queued" or "executing",
                Status = status,
                Result = ToResult(canonicalTurn?.Result),
                    User = new AgentSessionTranscriptUserDto
                {
                    Text = diagnostic
                        ? (string.IsNullOrWhiteSpace(turn.PromptText)
                            ? recoveryPrompt ?? string.Empty
                            : StripInternalPromptSections(turn.PromptText))
                        : PublicPromptText(turn, canonicalTurn, session?.Status.Inputs, recoveryPrompt),
                    Kind = diagnostic
                        ? AgentSessionJsonHelper.NormalizePromptKind(turn.PromptKind)
                        : PublicPromptKind(turn, canonicalTurn),
                    SentAt = at,
                    RuntimeSessionId = turn.RuntimeSessionId,
                },
            };
            toolPartIndex.Clear();

            if (parts is not null)
            {
                var partIndex = 0;
                var recoveryMarkerKeys = new HashSet<string>(StringComparer.Ordinal);
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
                        UpsertToolPart(dto, toolPartIndex, part, payload, partAt, ref partIndex, diagnostic);
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

                    if (part.Type == TranscriptPartTypes.SessionActivity)
                    {
                        var statusValue = AgentSessionJsonHelper.GetStringProp(payload, "status") ?? "completed";
                        if (statusValue is "failed" or "cancelled")
                        {
                            dto.Assistant.Add(new AgentSessionTranscriptPartDto
                            {
                                Id = $"{dto.Id}-p{++partIndex}",
                                Type = "error",
                                Message = AgentSessionJsonHelper.GetStringProp(payload, "failureReason") ?? $"Session {statusValue}",
                                Kind = statusValue == "cancelled" ? "cancelled" : "failed",
                                At = partAt,
                            });
                        }
                        continue;
                    }

                    if (part.Type == TranscriptPartTypes.SessionContextReset)
                    {
                        var reason = AgentSessionJsonHelper.GetStringProp(payload, "reason");
                        AddRecoveryMarker(
                            dto,
                            $"{dto.Id}-p{++partIndex}",
                            "context-reset",
                            string.IsNullOrWhiteSpace(reason)
                                ? "Subsequent turns use a new runtime context."
                                : $"Reason: {reason}. Subsequent turns use a new runtime context.",
                            partAt);
                        continue;
                    }

                    if (part.Type is TranscriptPartTypes.Compaction or "compaction_event")
                    {
                        var key = $"compaction:{part.PayloadJson}";
                        if (!recoveryMarkerKeys.Add(key))
                            continue;

                        var strategy = AgentSessionJsonHelper.GetStringProp(payload, "strategy");
                        var summary = AgentSessionJsonHelper.GetStringProp(payload, "summary");
                        var details = string.IsNullOrWhiteSpace(strategy) ? null : $"Strategy: {strategy}.";
                        var message = string.Join(
                            " ",
                            new[] { details, string.IsNullOrWhiteSpace(summary) ? null : summary }
                                .Where(value => !string.IsNullOrWhiteSpace(value)));
                        AddRecoveryMarker(
                            dto,
                            $"{dto.Id}-p{++partIndex}",
                            "compaction",
                            string.IsNullOrWhiteSpace(message) ? "Context history was compacted." : message,
                            partAt);
                        continue;
                    }

                    // Input is already represented by the turn's user projection;
                    // known operational facts and retired protocol events remain
                    // hidden from the public assistant transcript.
                    if (part.Type is TranscriptPartTypes.Input
                        or TranscriptPartTypes.Usage
                        or TranscriptPartTypes.Model
                        or RuntimeEventTypes.ContextHealthUpdate
                        or RuntimeEventTypes.ProviderRetry
                        || TranscriptAccumulator.IsRetiredEventType(part.Type))
                        continue;

                    dto.Assistant.Add(new AgentSessionTranscriptPartDto
                    {
                        Id = $"{dto.Id}-p{++partIndex}",
                        Type = "unknown",
                        Text = "Unknown runtime event",
                        Kind = "unknown",
                        StartedAt = partAt,
                        Raw = diagnostic
                            ? new AgentSessionTranscriptRawPartDto
                            {
                                Kind = "unknown",
                                Type = part.Type,
                                CorrelationKey = part.CorrelationKey,
                                CorrelationId = part.CorrelationId,
                                Text = SanitizeDiagnosticText(part.Text),
                                Payload = SanitizeDiagnosticPayload(payload),
                                PayloadJson = SanitizeDiagnosticPayload(payload).GetRawText(),
                                FirstSeenAt = part.FirstSeenAt.ToString("o"),
                                LastSeenAt = part.LastSeenAt.ToString("o"),
                                RawEventCount = part.RawEventCount,
                            }
                            : null,
                    });
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
            Activity = ActivityName(session?.Status.Activity, responseTurns),
            Status = ResolveResponseStatus(session, responseTurns),
        };
    }

    private static IEnumerable<AgentSessionTranscriptTurnRow> MissingCanonicalTurns(
        IReadOnlyList<AgentSessionTranscriptTurnRow> transcriptTurns,
        AgentSession? session)
    {
        if (session?.Status.Turns is not { Count: > 0 } canonicalTurns)
            yield break;

        var persistedSequences = transcriptTurns
            .Select(turn => turn.Sequence)
            .ToHashSet();
        var inputById = (session.Status.Inputs ?? [])
            .ToDictionary(input => input.Id, StringComparer.Ordinal);

        foreach (var canonicalTurn in canonicalTurns.OrderBy(turn => turn.Sequence))
        {
            if (persistedSequences.Contains(canonicalTurn.Sequence))
                continue;

            var recordedAt = canonicalTurn.RecordedAt ?? session.Status.CreatedAt;
            var prompt = string.Join(
                "\n",
                canonicalTurn.InputIds
                    .Where(inputById.ContainsKey)
                    .Select(inputId => inputById[inputId].Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            yield return new AgentSessionTranscriptTurnRow
            {
                Id = -Math.Max(1L, canonicalTurn.Sequence),
                SessionId = session.Id,
                RuntimeSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = canonicalTurn.Sequence,
                PromptText = prompt,
                PromptKind = canonicalTurn.Sequence > 1 ? "followup" : "task",
                StartedAt = recordedAt,
                UpdatedAt = canonicalTurn.UpdatedAt ?? recordedAt,
            };
        }
    }

    private static string PublicPromptText(
        AgentSessionTranscriptTurnRow turn,
        AgentTurnRecord? canonicalTurn,
        IReadOnlyList<AgentSessionInputRecord>? inputs,
        string? recoveryPrompt)
    {
        if (canonicalTurn is not null && inputs is not null)
        {
            var inputById = inputs.ToDictionary(input => input.Id, StringComparer.Ordinal);
            var texts = canonicalTurn.InputIds
                .Where(inputById.ContainsKey)
                .Select(inputId => inputById[inputId].Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            if (texts.Length > 0) return string.Join("\n", texts);
            if (canonicalTurn.InputIds.Count > 0) return "Attachment input";
        }

        var sanitized = StripInternalPromptSections(turn.PromptText);
        return string.IsNullOrWhiteSpace(sanitized)
            ? recoveryPrompt ?? "Task input recorded"
            : sanitized;
    }

    private static bool IsDiagnosticView(string? view) =>
        string.Equals(view, "raw", StringComparison.OrdinalIgnoreCase)
        || string.Equals(view, "diagnostic", StringComparison.OrdinalIgnoreCase);

    private static string PublicPromptKind(AgentSessionTranscriptTurnRow turn, AgentTurnRecord? canonicalTurn) =>
        canonicalTurn?.Sequence > 1
            ? "followup"
            : AgentSessionJsonHelper.NormalizePromptKind(turn.PromptKind);

    private static string ResolveTurnStatus(
        AgentTurnRecord? canonicalTurn,
        IReadOnlyList<AgentSessionTranscriptPartRow>? parts,
        AgentSessionActivity? activity)
    {
        if (canonicalTurn is not null) return TurnStatus(canonicalTurn.Status);
        if (parts?.Any(part => part.Type == TranscriptPartTypes.SessionActivity
            && AgentSessionJsonHelper.GetStringProp(AgentSessionJsonHelper.ParsePayloadOrEmpty(part.PayloadJson), "status") == "failed") == true)
            return "failed";
        if (activity == AgentSessionActivity.Unknown) return "unknown";
        if (activity == AgentSessionActivity.Active) return "executing";
        return "completed";
    }

    private static string ResolveResponseStatus(
        AgentSession? session,
        IReadOnlyList<AgentSessionTranscriptTurnDto> turns)
    {
        var current = session?.Status.Turns?
            .OrderByDescending(turn => turn.Sequence)
            .FirstOrDefault(turn => turn.Status is AgentTurnStatus.Queued
                or AgentTurnStatus.Executing
                or AgentTurnStatus.Unknown);
        if (current is not null) return TurnStatus(current.Status);
        if (turns.Count > 0) return turns[^1].Status;
        return session?.Status.Activity switch
        {
            AgentSessionActivity.Active => "executing",
            AgentSessionActivity.Unknown => "unknown",
            _ => "completed",
        };
    }

    private static string ActivityName(AgentSessionActivity? activity, IReadOnlyList<AgentSessionTranscriptTurnDto> turns) =>
        activity switch
        {
            AgentSessionActivity.Active => "active",
            AgentSessionActivity.Unknown => "unknown",
            AgentSessionActivity.Idle => "idle",
            _ => turns.Any(turn => turn.Status is "queued" or "executing") ? "active" : "unknown",
        };

    private static string TurnStatus(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Queued => "queued",
        AgentTurnStatus.Executing => "executing",
        AgentTurnStatus.Completed => "completed",
        AgentTurnStatus.Failed => "failed",
        AgentTurnStatus.Cancelled => "cancelled",
        AgentTurnStatus.Unknown => "unknown",
        _ => "unknown",
    };

    private static AgentTurnResultObservationDto? ToResult(AgentTurnResult? result) => result is null
        ? null
        : new AgentTurnResultObservationDto(
            result.Message,
            result.Output,
            result.FailureReason,
            result.FailureCategory,
            result.ExitCode);

    private static string StripInternalPromptSections(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;
        var value = prompt;
        var removedInternalSection = false;
        foreach (var marker in new[]
        {
            "mohist-agent-session-startup",
            "mohist-workspace-anchor",
            "mohist-execution-definition",
            "mohist-system-facts",
        })
        {
            var before = value;
            value = RemoveMarkedSection(value, marker);
            removedInternalSection |= !string.Equals(before, value, StringComparison.Ordinal);
        }

        const string parentPrefix = "Parent issue context (read-only background; JSON):";
        var parentStart = value.IndexOf(parentPrefix, StringComparison.Ordinal);
        if (parentStart >= 0)
        {
            var taskStart = value.IndexOf("\n\n", parentStart, StringComparison.Ordinal);
            taskStart = taskStart < 0 ? -1 : value.IndexOf("\n\n", taskStart + 2, StringComparison.Ordinal);
            value = taskStart < 0 ? string.Empty : value[(taskStart + 2)..];
        }

        if (removedInternalSection)
        {
            var paragraphs = value
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paragraphs.Length > 1)
                value = paragraphs[^1];
        }

        return value.Trim();
    }

    private static string RemoveMarkedSection(string value, string marker)
    {
        var opening = $"[{marker}]";
        var closing = $"[/{marker}]";
        while (true)
        {
            var start = value.IndexOf(opening, StringComparison.Ordinal);
            if (start < 0) return value;
            var end = value.IndexOf(closing, start + opening.Length, StringComparison.Ordinal);
            value = end < 0
                ? value[..start]
                : value.Remove(start, end + closing.Length - start);
        }
    }

    private static string? RecoveryPromptText(IReadOnlyList<AgentSessionTranscriptPartRow> parts)
    {
        var reset = parts.FirstOrDefault(part => part.Type == TranscriptPartTypes.SessionContextReset);
        if (reset is not null)
        {
            var reason = AgentSessionJsonHelper.GetStringProp(
                AgentSessionJsonHelper.ParsePayloadOrEmpty(reset.PayloadJson),
                "reason");
            return string.IsNullOrWhiteSpace(reason) ? "Context reset" : $"Context reset: {reason}";
        }

        return parts.Any(part => part.Type is TranscriptPartTypes.Compaction or "compaction_event")
            ? "Context compaction"
            : null;
    }

    private static void AddRecoveryMarker(
        AgentSessionTranscriptTurnDto turn,
        string id,
        string kind,
        string message,
        string at) =>
        turn.Assistant.Add(new AgentSessionTranscriptPartDto
        {
            Id = id,
            Type = "error",
            Message = message,
            Kind = kind,
            At = at,
        });

    private static void UpsertToolPart(
        AgentSessionTranscriptTurnDto turn,
        IDictionary<string, AgentSessionTranscriptPartDto> toolPartIndex,
        AgentSessionTranscriptPartRow partRow,
        JsonElement payload,
        string at,
        ref int partIndex,
        bool diagnostic)
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
            if (diagnostic)
            {
                existing.Tool.Input = rawInput ?? existing.Tool.Input;
                existing.Tool.Output = rawOutput ?? existing.Tool.Output;
                existing.Tool.RawInput = rawInput ?? existing.Tool.RawInput;
                existing.Tool.RawOutput = rawOutput ?? existing.Tool.RawOutput;
            }
            existing.Tool.Error = status == "failed"
                ? PublicToolError(payload, rawOutput, diagnostic)
                : existing.Tool.Error;
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
                Input = diagnostic ? rawInput : null,
                Output = diagnostic ? rawOutput : null,
                RawInput = diagnostic ? rawInput : null,
                RawOutput = diagnostic ? rawOutput : null,
                Error = status == "failed" ? PublicToolError(payload, rawOutput, diagnostic) : null,
                StartedAt = at,
                CompletedAt = completedAt,
            },
        };
        toolPartIndex[toolCallId] = part;
        turn.Assistant.Add(part);
    }

    private static string PublicToolError(JsonElement payload, string? rawOutput, bool diagnostic)
    {
        if (diagnostic) return rawOutput ?? "Tool failed";
        return AgentSessionJsonHelper.GetToolStringProp(payload, "error")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "failureReason")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "message")
            ?? "Tool failed";
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
        if (TryGetRaw(payload, name, out var raw)) return SanitizeToolRaw(raw);
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall)
            && TryGetRaw(toolCall, name, out raw))
            return SanitizeToolRaw(raw);
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

    private static string SanitizeToolRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return SanitizeDiagnosticPayload(document.RootElement).GetRawText();
        }
        catch (JsonException)
        {
            return SanitizeDiagnosticText(raw);
        }
    }

    private static JsonElement SanitizeDiagnosticPayload(JsonElement payload)
    {
        var node = JsonNode.Parse(payload.GetRawText());
        var sanitized = SanitizeDiagnosticNode(node);
        using var document = JsonDocument.Parse(sanitized?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static JsonNode? SanitizeDiagnosticNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveDiagnosticKey(property.Key))
                {
                    jsonObject.Remove(property.Key);
                    continue;
                }

                jsonObject[property.Key] = SanitizeDiagnosticNode(property.Value);
            }

            return jsonObject;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
                jsonArray[index] = SanitizeDiagnosticNode(jsonArray[index]);
            return jsonArray;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return JsonValue.Create(SanitizeDiagnosticText(text));

        return node;
    }

    private static bool IsSensitiveDiagnosticKey(string key)
    {
        var compact = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return compact.Contains("prompt", StringComparison.Ordinal)
            || compact.Contains("memory", StringComparison.Ordinal)
            || compact.Contains("workspace", StringComparison.Ordinal)
            || compact.Contains("workdir", StringComparison.Ordinal)
            || compact.Contains("rawinput", StringComparison.Ordinal)
            || compact.Contains("rawoutput", StringComparison.Ordinal)
            || compact is "path" or "filepath" or "oldpath" or "cwd" or "system";
    }

    private static string SanitizeDiagnosticText(string? text) =>
        StripInternalPromptSections(text).Trim();
}
