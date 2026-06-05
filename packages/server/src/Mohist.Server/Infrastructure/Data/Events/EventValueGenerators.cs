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
        if (row.WorkflowEvent is null)
            throw new InvalidOperationException("EventRow.WorkflowEvent is required to generate event type");
        return WorkflowEventSerializer.Type(row.WorkflowEvent.Value);
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
