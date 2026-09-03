using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Domain;

[Trait("level", "L0")]
public sealed class HistoricalInterruptionEventCompatibilityTests
{
    [Fact]
    public void TaskAndChecksInterruptionEventsRemainReadableAfterCurrentStateRetirement()
    {
        var deadline = DateTimeOffset.Parse("2026-08-01T00:15:00+00:00");
        WorkflowEvent[] historical =
        [
            new TaskInterrupted("build", "build.1", "work-1", "runner-lost", deadline),
            new ChecksInterrupted("check", "checks:check", "runner-lost", deadline),
        ];

        Assert.Collection(
            historical,
            task =>
            {
                Assert.Equal(EventCatalog.ReverseDns.TaskInterrupted, WorkflowEventSerializer.BusType(task));
                Assert.Equal(
                    WorkflowEventSerializer.Unwrap(task),
                    WorkflowEventSerializer.Unwrap(WorkflowEventSerializer.FromData(
                        nameof(TaskInterrupted),
                        WorkflowEventSerializer.ToData(task))));
            },
            checks =>
            {
                Assert.Equal(EventCatalog.ReverseDns.ChecksInterrupted, WorkflowEventSerializer.BusType(checks));
                Assert.Equal(
                    WorkflowEventSerializer.Unwrap(checks),
                    WorkflowEventSerializer.Unwrap(WorkflowEventSerializer.FromData(
                        nameof(ChecksInterrupted),
                        WorkflowEventSerializer.ToData(checks))));
            });
    }
}
