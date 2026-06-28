using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mohist.Server.Infrastructure.Data.Runner;

public class RunnerWorkRow
{
    public long Id { get; set; }

    [MaxLength(256)]
    public string RunnerId { get; set; } = string.Empty;

    [MaxLength(16)]
    public string OwnerKind { get; set; } = string.Empty;

    [MaxLength(256)]
    public string OwnerId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string WorkId { get; set; } = string.Empty;

    public DateTimeOffset TakenAt { get; set; }

    [MaxLength(16)]
    public string Status { get; set; } = "outstanding";

    [MaxLength(256)]
    public string? Reason { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
