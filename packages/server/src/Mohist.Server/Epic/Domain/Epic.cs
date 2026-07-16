using System.Text.Json.Serialization;
using Mohist.Server.Epic.Domain.Events;

namespace Mohist.Server.Epic.Domain;

public sealed partial class Epic
{
    private string _title = null!;
    private string _description = "";
    private EpicPriority _priority = EpicPriority.Default;
    private EpicStatus _status;
    private string? _pauseReason;
    private DateTime _createdAt;
    private DateTime _updatedAt;
    private readonly List<EpicEvent> _pendingEvents = new();

    public required string ProjectId { get; init; }
    public required int Number { get; init; }

    public required string Title
    {
        get => _title;
        init => _title = RequireTitle(value);
    }

    public string Description
    {
        get => _description;
        init => _description = value ?? "";
    }

    public string Priority
    {
        get => _priority.Value;
        init => _priority = EpicPriority.From(value);
    }

    public EpicStatus Status
    {
        get => _status;
        init => _status = value;
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        init => _createdAt = value;
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        init => _updatedAt = value;
    }

    public string? PauseReason
    {
        get => _pauseReason;
        init => _pauseReason = value;
    }

    [JsonIgnore]
    public IReadOnlyList<EpicEvent> PendingEvents => _pendingEvents;

    public void ClearPendingEvents() => _pendingEvents.Clear();

    private void RecordEvent(EpicEvent evt) => _pendingEvents.Add(evt);

    private static string RequireTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Epic title is required", nameof(title));
        return title;
    }

    private void Touch(DateTime? now = null)
    {
        _updatedAt = now ?? DateTime.UtcNow;
    }
}
