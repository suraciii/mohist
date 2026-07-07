using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events.Subscriptions;

/// <summary>
/// Unit specs for <see cref="SubscriptionFilter"/>.Matches — the envelope
/// matcher used by the subscription dispatch pipeline (issue-391 T-003).
/// The matcher is envelope-only (no Workflow / Issue domain reads) and
/// runs at high frequency, so the suite stays in pure unit territory with
/// no fixture, no DB, no grain. Each scenario maps directly to a spec
/// scenario in <c>specs/agent-subscription-dispatch/spec.md</c>.
/// </summary>
public class SubscriptionFilterSpecs
{
    // === Type field ===

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_ExactType_MatchesWhenTypeEquals()
    {
        var filter = new SubscriptionFilter { Type = "com.mohist.workflow.stage.approval-requested" };
        var evt = BuildEvent(type: "com.mohist.workflow.stage.approval-requested");

        Assert.True(filter.Matches(evt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_ExactType_DoesNotMatchDifferentType()
    {
        var filter = new SubscriptionFilter { Type = "com.mohist.workflow.stage.approval-requested" };
        var evt = BuildEvent(type: "com.mohist.workflow.stage.completed");

        Assert.False(filter.Matches(evt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_PipeAlternatives_MatchesAnyListedType()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.workflow.stage.approval-requested|com.mohist.workflow.run.failed",
        };

        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.approval-requested")));
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.run.failed")));
        Assert.False(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.completed")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_Star_MatchesAnyType()
    {
        var filter = new SubscriptionFilter { Type = "*" };

        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.approval-requested")));
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.issue.completed")));
        Assert.True(filter.Matches(BuildEvent(type: "anything.you.like")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_PrefixDotStar_MatchesPrefixAndSubTypes()
    {
        var filter = new SubscriptionFilter { Type = "com.mohist.workflow.stage.*" };

        // The prefix itself matches.
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage")));
        // Any prefix.<anything> matches.
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.approval-requested")));
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.completed")));
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.workflow.stage.x.y.z")));
        // Different prefix does not match.
        Assert.False(filter.Matches(BuildEvent(type: "com.mohist.workflow.run.failed")));
        // Plain substring (no dot boundary) does NOT match.
        Assert.False(filter.Matches(BuildEvent(type: "com.mohist.workflow.stageextra")));
        // Also does not match strings that contain the prefix without a
        // dot boundary (the spec explicitly excludes this).
        Assert.False(filter.Matches(BuildEvent(type: "xcom.mohist.workflow.stage")));
    }

    // === Source field ===

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_SourceConstraint_MatchesOnlyMatchingSource()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.workflow.stage.*",
            Source = "/mohist/workflow-runs/run_specific",
        };

        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_specific")));
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_other")));
        // Type matches but source does not → does not match.
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.completed",
            source: "/mohist/workflow-runs/run_other")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_SourceConstraint_NullIsNoConstraint()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.workflow.stage.*",
            Source = null,
        };

        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_a")));
        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_b")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_SourceConstraint_WhitespaceIsNoConstraint()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.workflow.stage.*",
            Source = "   ",
        };

        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_a")));
    }

    // === Subject field ===

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_SubjectConstraint_MatchesOnlyMatchingSubject()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.issue.*",
            Subject = "42",
        };

        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.issue.work-started",
            subject: "42")));
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.issue.work-started",
            subject: "43")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_SubjectConstraint_NullIsNoConstraint()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.issue.*",
            Subject = null,
        };

        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.issue.completed", subject: "1")));
        Assert.True(filter.Matches(BuildEvent(type: "com.mohist.issue.completed", subject: "9999")));
    }

    // === Defensive ===

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_NullEvent_ReturnsFalse()
    {
        var filter = new SubscriptionFilter { Type = "*" };
        Assert.False(filter.Matches(null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_EmptyTypePattern_DoesNotMatchAnyEvent()
    {
        var filter = new SubscriptionFilter { Type = string.Empty };
        Assert.False(filter.Matches(BuildEvent(type: "any.event")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public void Matches_TypeSourceSubjectCompound_AllMustMatch()
    {
        var filter = new SubscriptionFilter
        {
            Type = "com.mohist.workflow.stage.*",
            Source = "/mohist/workflow-runs/run_target",
            Subject = "issue-42",
        };

        // All three constraints satisfied.
        Assert.True(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_target",
            subject: "issue-42")));
        // Source mismatch → no match (subject still equal).
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_other",
            subject: "issue-42")));
        // Subject mismatch → no match (source still equal).
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_target",
            subject: "issue-99")));
        // Subject missing on event → subject constraint fails.
        Assert.False(filter.Matches(BuildEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/workflow-runs/run_target",
            subject: null)));
    }

    private static CloudEvent BuildEvent(
        string type = "com.mohist.workflow.stage.approval-requested",
        string source = "/mohist/workflow-runs/run_x",
        string? subject = null,
        JsonElement? data = null) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: data,
            subject: subject);
}