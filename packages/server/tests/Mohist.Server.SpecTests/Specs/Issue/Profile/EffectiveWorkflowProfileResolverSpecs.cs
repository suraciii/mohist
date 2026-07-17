using Mohist.Server.Issue.Services.WorkflowProfiles;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

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

    [Fact]
    public void ResolveCore_NullIssueSelection_NoProjectDefault_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: null,
            exists: _ => true);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void ResolveCore_EmptyWhitespaceIssueSelection_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "   ",
            projectDefaultId: null,
            exists: _ => true);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void ResolveCore_ExplicitIssueSelection_TakesPrecedenceOverProjectDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "mohist/github-pr",
            projectDefaultId: "mohist/local",
            exists: _ => true);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void ResolveCore_NoIssueSelection_UsesProjectDefaultWhenKnown()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: "mohist/github-pr",
            exists: id => id == "mohist/github-pr");

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void ResolveCore_UnknownIssueSelection_FallsThroughToProjectDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "team/custom",
            projectDefaultId: "mohist/github-pr",
            exists: id => id == "mohist/github-pr" || id == IssueWorkflowProfiles.LocalId);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void ResolveCore_UnknownIssueSelection_AndUnknownProjectDefault_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "team/custom",
            projectDefaultId: "team/custom-default",
            exists: id => id == IssueWorkflowProfiles.LocalId);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void ResolveCore_UnknownProjectDefaultAlone_FallsToSystemDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: "team/custom-default",
            exists: id => id == IssueWorkflowProfiles.LocalId);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    // ===================== Service (registry-backed) =====================

    [Fact]
    public void Resolve_NullSelection_AndNoProjectDefault_ReturnsMohistLocal()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: null, projectDefaultId: null);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void Resolve_ExplicitPrSelection_ReturnsMohistPr()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "mohist/github-pr", projectDefaultId: null);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void Resolve_NullSelection_WithPrProjectDefault_ReturnsMohistPr()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: null, projectDefaultId: "mohist/github-pr");

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void Resolve_ExplicitDefaultSelection_ReturnsMohistLocal()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "mohist/local", projectDefaultId: "mohist/github-pr");

        Assert.Equal("mohist/local", resolved);
    }

    [Fact]
    public void Resolve_UnknownId_DoesNotThrowAndFallsBackToSystemDefault()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(issueSelection: "team/missing", projectDefaultId: null);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    // ===================== Disabled-profile skipping (core) =====================

    [Fact]
    public void ResolveCore_DisabledIssueSelection_FallsThroughToProjectDefault()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "MOHIST/GITHUB-PR",
            projectDefaultId: "mohist/local",
            exists: id => id.Equals("mohist/github-pr", StringComparison.OrdinalIgnoreCase)
                || id.Equals("mohist/local", StringComparison.OrdinalIgnoreCase),
            disabledIds: new[] { "mohist/github-pr" },
            systemProfileIds: ["mohist/local", "mohist/github-pr"]);

        Assert.Equal("mohist/local", resolved);
    }

    [Fact]
    public void ResolveCore_DisabledIssueSelectionAndProjectDefault_FallsThroughToFirstEnabledSystem()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: "mohist/github-pr",
            projectDefaultId: "mohist/local",
            exists: _ => true,
            disabledIds: new[] { "mohist/github-pr", "mohist/local" },
            systemProfileIds: ["mohist/local", "mohist/github-pr"]);

        // both are disabled, so both are skipped — no enabled system profile
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveCore_DisabledProjectDefault_WithOtherEnabledSystem_SkipsToOther()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: "mohist/local",
            exists: _ => true,
            disabledIds: new[] { "mohist/local" },
            systemProfileIds: ["mohist/local", "mohist/github-pr"]);

        Assert.Equal("mohist/github-pr", resolved);
    }

    [Fact]
    public void ResolveCore_NoDisabledIds_BehavesAsBefore()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: null,
            exists: _ => true);

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void ResolveCore_BlacklistAwareWithoutSystemProfileIds_ReturnsNull()
    {
        var resolved = EffectiveWorkflowProfileResolver.ResolveCore(
            issueSelection: null,
            projectDefaultId: null,
            exists: _ => true,
            disabledIds: ["mohist/local"]);

        Assert.Null(resolved);
    }

    // ===================== Instance Resolve with disabled set =====================

    [Fact]
    public void Resolve_WithDisabledSet_SkipsDisabledProfiles()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(
            issueSelection: "mohist/github-pr",
            projectDefaultId: null,
            disabledIds: new[] { "mohist/github-pr" });

        Assert.Equal(IssueWorkflowProfiles.LocalId, resolved);
    }

    [Fact]
    public void Resolve_AllSystemProfilesDisabled_ReturnsNull()
    {
        var resolver = BuildResolver();

        var resolved = resolver.Resolve(
            issueSelection: null,
            projectDefaultId: null,
            disabledIds: new[] { "mohist/local", "mohist/github-pr" });

        Assert.Null(resolved);
    }
}
