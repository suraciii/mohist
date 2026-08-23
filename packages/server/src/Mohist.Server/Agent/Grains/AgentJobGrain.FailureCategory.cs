using System.Text.Json;

namespace Mohist.Server.Agent.Grains;

// Failure-category extraction split from AgentJobGrain to keep the main partial
// within the file-size ratchet.
public sealed partial class AgentJobGrain
{
    private static string? FailureCategoryFromOutput(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } element) return null;
        return element.TryGetProperty("failureCategory", out var category)
            && category.ValueKind == JsonValueKind.String
            ? category.GetString()
            : null;
    }

    private static string? FailureCategoryFromErrorCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code;

    private static string? FailureCategoryFromStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null : status;
}
