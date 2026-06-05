using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class EventRow
{
    public required long Id { get; init; }
    public required string Source { get; init; }
    public required JsonElement Data { get; init; }
    public DateTime Time { get; init; }

    [NotMapped]
    public WorkflowEvent? WorkflowEvent { get; init; }

    [NotMapped]
    public AgentSessionEvent? AgentSessionEvent { get; init; }
}
