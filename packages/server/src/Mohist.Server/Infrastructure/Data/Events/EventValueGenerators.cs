using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class EventTypeGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => false;

    public override string Next(EntityEntry entry)
    {
        var row = (EventRow)entry.Entity;
        if (row.WorkflowEvent is not null)
            return WorkflowEventSerializer.Type(row.WorkflowEvent.Value);
        if (row.AgentSessionEvent is not null)
            return AgentSessionEventSerializer.Type(row.AgentSessionEvent.Value);
        throw new InvalidOperationException("EventRow requires a domain event to generate event type");
    }
}

public sealed class EventTimeGenerator : ValueGenerator<DateTime>
{
    public override bool GeneratesTemporaryValues => false;

    public override DateTime Next(EntityEntry entry) => DateTime.UtcNow;
}

public sealed class EventSpecVersionGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => false;

    public override string Next(EntityEntry entry) => "1.0";
}
