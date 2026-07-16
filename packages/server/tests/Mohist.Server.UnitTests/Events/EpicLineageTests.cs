using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public class EpicLineageTests
{
    [Fact]
    public void BuildExtensions_StampsProjectIdAndEpicNumberOnly()
    {
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_lineage",
            Number = 7,
            Title = "Any title",
        };

        var extensions = EpicLineage.BuildExtensions(state);

        Assert.Equal("proj_lineage", extensions["projectid"]);
        Assert.Equal("7", extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public void BuildExtensions_OmitsEpicNoKey()
    {
        // Epic events route by the project-scoped epic number. The
        // dictionary must not carry the superseded "epicno" alias.
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_no_epicno",
            Number = 99,
            Title = "Any title",
        };

        var extensions = EpicLineage.BuildExtensions(state);

        Assert.False(extensions.ContainsKey("epicno"));
    }

    [Fact]
    public void BuildExtensions_OnlyStampsTwoKeysForEpic()
    {
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_exact_two",
            Number = 1,
            Title = "Any title",
        };

        var extensions = EpicLineage.BuildExtensions(state);

        Assert.Equal(2, extensions.Count);
    }

    [Theory]
    [InlineData(EventCatalog.ReverseDns.EpicCreated)]
    [InlineData(EventCatalog.ReverseDns.EpicUpdated)]
    [InlineData(EventCatalog.ReverseDns.EpicPriorityChanged)]
    [InlineData(EventCatalog.ReverseDns.EpicIssueLinked)]
    [InlineData(EventCatalog.ReverseDns.EpicIssueUnlinked)]
    [InlineData(EventCatalog.ReverseDns.EpicStatusChanged)]
    [InlineData(EventCatalog.ReverseDns.EpicClosed)]
    [InlineData(EventCatalog.ReverseDns.EpicReopened)]
    [InlineData(EventCatalog.ReverseDns.EpicStartAttemptFailed)]
    public void BuildExtensions_SatisfiesEventCatalogForEveryEpicType(string eventType)
    {
        // The helper's output is the single source extensions a producer
        // writes onto every epic.* envelope. For each registered epic
        // event type, the helper's dictionary must satisfy the catalog's
        // required-attribute matrix — no missing keys, no empty values.
        // A failure here means the producer drifted from the registry:
        // the conformance check would catch it on the live path too.
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_conformance",
            Number = 5,
            Title = "Any title",
        };

        var extensions = EpicLineage.BuildExtensions(state);

        var missing = EnvelopeConformance.Missing(extensions, eventType);
        Assert.Empty(missing);
    }

    [Fact]
    public void BuildExtensions_AssertRequiredDoesNotThrow_ForAnEpicType()
    {
        // The throwing variant must also accept the helper's output for a
        // representative epic type (covers the protocol-named line: the
        // dispatcher catches drift in CI rather than at the live call
        // site).
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_assert_required",
            Number = 1,
            Title = "Any title",
        };
        var extensions = EpicLineage.BuildExtensions(state);

        EnvelopeConformance.AssertRequired(extensions, EventCatalog.ReverseDns.EpicIssueLinked);
    }

    [Fact]
    public void BuildExtensions_StampsFromEpicStateWithoutAnyCrossAggregateLoad()
    {
        // Mirrors WorkflowRunLineage / IssueLineage: the helper takes only
        // the producing aggregate's state. Stamp source is the value-passed
        // fields — no DB context, no grain call. A future refactor that
        // added a query against EpicIssues would change this constructor's
        // arity and surface here.
        var state = new Mohist.Server.Epic.Domain.Epic
        {
            ProjectId = "proj_pure_helper",
            Number = 42,
            Title = "Any title",
        };
        var extensions = EpicLineage.BuildExtensions(state);

        Assert.Equal("42", extensions[EventCatalog.Lineage.Epic]);
        Assert.Equal("proj_pure_helper", extensions[EventCatalog.Lineage.ProjectId]);
        // Pin against a stamp CloudEvent-shaped artifact so any added
        // accidental key is visible (e.g. a future "epicno" re-add).
        // The dictionary MUST contain exactly these two keys in whatever
        // order insertion happened to land — set equality is what protects
        // the producer from accidental key drift.
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "projectid", "epic" },
            new HashSet<string>(extensions.Keys, StringComparer.Ordinal));
    }
}
