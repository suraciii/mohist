using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueRow
{
    private string _state = "{}";

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            PopulateIdentityFromState();
        }
    }

    public string? ProjectId { get; set; }
    public int? Number { get; set; }
    public string? Status { get; set; }
    public string? WorkflowRunId { get; set; }
    public bool? IsArchived { get; set; }
    public string? Title { get; set; }
    public string? Priority { get; set; }
    public bool? IsDraft { get; set; }
    public string? PrerequisiteNumbersJson { get; set; }
    public string? Risk { get; set; }
    public int? EpicNumber { get; set; }

    private void PopulateIdentityFromState()
    {
        if (ProjectId is not null && Number is not null) return;

        using var document = JsonDocument.Parse(_state);
        var state = document.RootElement;

        if (ProjectId is null
            && TryGetProperty(state, "projectId", "ProjectId", out var projectId)
            && projectId.ValueKind == JsonValueKind.String)
            ProjectId = projectId.GetString();

        if (Number is null
            && TryGetProperty(state, "number", "Number", out var number)
            && number.TryGetInt32(out var parsedNumber))
            Number = parsedNumber;
    }

    private static bool TryGetProperty(JsonElement value, string camelCase, string pascalCase, out JsonElement property) =>
        value.TryGetProperty(camelCase, out property) || value.TryGetProperty(pascalCase, out property);
}
