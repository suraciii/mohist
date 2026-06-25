using Mohist.Server.Issue.Services.WorkflowProfiles;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Issue.Profile;

/// <summary>
/// Covers <see cref="EffectiveWorkflowProfileResolver"/> — the single
/// source of truth for an issue's effective workflow profile id used by
/// every read surface (issue detail, list, workflow-profile endpoint,
/// <c>mo issue show</c>).
/// </summary>
public class EffectiveWorkflowProfileResolverSpecs
{
    private static EffectiveWorkflowProfileResolver BuildResolver() =>
        new(BuildRegistry());

    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new(new FakePromptLoader(), new FakeDbContextFactory());

    // ===================== Pure core (existence-only) =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_NullIssueSelection_NoProjectDefault_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: null,
            exists: _ => true);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_EmptyWhitespaceIssueSelection_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "   ",
            projectDefaultId: null,
            exists: _ => true);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_ExplicitIssueSelection_TakesPrecedenceOverProjectDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "mohist/github-pr",
            projectDefaultId: "mohist/default",
            exists: _ => true);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_NoIssueSelection_UsesProjectDefaultWhenKnown()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: "mohist/github-pr",
            exists: id => id == "mohist/github-pr");

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_UnknownIssueSelection_FallsThroughToProjectDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "team/custom",
            projectDefaultId: "mohist/github-pr",
            exists: id => id == "mohist/github-pr" || id == IssueWorkflowProfiles.DefaultId);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_UnknownIssueSelection_AndUnknownProjectDefault_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "team/custom",
            projectDefaultId: "team/custom-default",
            exists: id => id == IssueWorkflowProfiles.DefaultId);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ResolveCore_UnknownProjectDefaultAlone_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: "team/custom-default",
            exists: id => id == IssueWorkflowProfiles.DefaultId);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }

    // ===================== Service (registry-backed) =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resolve_NullSelection_AndNoProjectDefault_ReturnsMohistDefault()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: null, projectDefaultId: null);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resolve_ExplicitPrSelection_ReturnsMohistPr()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "mohist/github-pr", projectDefaultId: null);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resolve_NullSelection_WithPrProjectDefault_ReturnsMohistPr()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: null, projectDefaultId: "mohist/github-pr");

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resolve_ExplicitDefaultSelection_ReturnsMohistDefault()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "mohist/default", projectDefaultId: "mohist/github-pr");

        Assert.Equal("mohist/default", resolved);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resolve_UnknownId_DoesNotThrowAndFallsBackToSystemDefault()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "team/missing", projectDefaultId: null);

        Assert.Equal(IssueWorkflowProfiles.DefaultId, resolved);
    }
}
