using CloudNative.CloudEvents;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class CloudEventFactorySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Create_PopulatesAllRequiredAttributes()
    {
        var evt = CloudEventFactory.Create(
            type: "com.mohist.workflow.run.completed",
            source: new Uri("/mohist/workflow/wr-1", UriKind.Relative),
            data: new { workflowRunId = "wr-1" },
            subject: "42",
            projectId: "mohist",
            workflowRunId: "wr-1",
            issueNumber: "42");

        Assert.Equal("com.mohist.workflow.run.completed", evt.Type);
        Assert.Equal("1.0", evt.SpecVersion.VersionId);
        Assert.Equal("/mohist/workflow/wr-1", evt.Source?.ToString());
        Assert.Equal("42", evt.Subject);
        Assert.NotNull(evt.Id);
        Assert.NotNull(evt.Time);

        var ext = evt.GetPopulatedAttributes()
            .Where(a => a.Key.IsExtension)
            .ToDictionary(a => a.Key.Name, a => a.Value);
        Assert.Equal("mohist", ext["projectid"]);
        Assert.Equal("wr-1", ext["workflowrunid"]);
        Assert.Equal("42", ext["issueno"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Create_LiftsProjectIdFromIProjectScopedPayload()
    {
        var payload = new StageChangedEvent(
            "implicit-project", "wr-1", "plan", "Running", "started", null, "2026-06-07T00:00:00Z");

        var evt = CloudEventFactory.Create(
            type: "stage_changed",
            source: new Uri("/mohist/workflow/wr-1", UriKind.Relative),
            data: payload);

        var ext = evt.GetPopulatedAttributes()
            .Where(a => a.Key.IsExtension)
            .ToDictionary(a => a.Key.Name, a => a.Value);
        Assert.Equal("implicit-project", ext["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void EventCatalog_AllTypesAreNonEmpty()
    {
        Assert.NotEmpty(EventCatalog.All);
        Assert.All(EventCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void EventCatalog_ReverseDnsConstants_AreNonEmpty()
    {
        var consts = typeof(EventCatalog.ReverseDns)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(consts);
        Assert.All(consts, c => Assert.StartsWith("com.mohist.", c));
    }
}
