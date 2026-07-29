using System.Text.RegularExpressions;
using Xunit;

namespace Mohist.Server.ArchTests;

/// <summary>
/// issue-511 T-001: structural contract that <c>WorkflowGrain</c>
/// production code carries no dead event-dispatch path,
/// no settable coordinator-bypass hook, and no
/// <c>ex.Message.Contains(...)</c> branching in <c>CommitAsync</c>.
/// Together with the typed
/// <c>WorkflowDefinitionResolutionException</c> this guarantees
/// reworded resolution-exception messages cannot silently change
/// control flow.
/// </summary>
public sealed class WorkflowGrainContractRules
{
    private const string WorkflowGrainsDir = "Workflow/Grains/";
    private const string WorkflowServicesDir = "Workflow/Services/";

    [Fact]
    public void DispatchEvent_IsNotDeclaredInWorkflowGrainContext()
    {
        var source = ReadEmbeddedFile(WorkflowGrainsDir + "IWorkflowGrainContext.cs");
        AssertSourceHasNoDispatchEventDeclaration(source);
    }

    [Fact]
    public void DispatchEvent_IsNotImplementedInWorkflowGrain()
    {
        var source = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowGrain.cs");
        AssertSourceHasNoDispatchEventDeclaration(source);
    }

    [Fact]
    public void DispatchEvent_IsNotCalledFromWorkflowWorkLifecycle()
    {
        var source = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowWorkLifecycle.cs");
        AssertSourceHasNoDispatchEventDeclaration(source);
    }

    [Fact]
    public void WorkflowGrain_HasNoOnDispatchMethod()
    {
        var source = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowGrain.cs");

        Assert.DoesNotContain("Task On(WorkflowEvent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private Task On(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowGrain_DoesNotExposeBindProfileForTestSettableDelegate()
    {
        var grain = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowGrain.cs");
        var grainContext = ReadEmbeddedFile(WorkflowGrainsDir + "IWorkflowGrainContext.cs");

        Assert.DoesNotContain("BindProfileForTest", grain, StringComparison.Ordinal);
        Assert.DoesNotContain("BindProfileForTest", grainContext, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowGrain_CommitAsync_DoesNotBranchOnExceptionMessageSubstring()
    {
        var grain = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowGrain.cs");

        var commitIdx = grain.IndexOf("private async Task CommitAsync(", StringComparison.Ordinal);
        Assert.True(commitIdx >= 0, "WorkflowGrain.cs must contain a CommitAsync method");

        var endCandidates = new[]
        {
            grain.IndexOf("\n    private ", commitIdx + 1, StringComparison.Ordinal),
            grain.IndexOf("\n    internal ", commitIdx + 1, StringComparison.Ordinal),
            grain.IndexOf("\n    public ", commitIdx + 1, StringComparison.Ordinal),
        }.Where(i => i > 0).DefaultIfEmpty(grain.Length).Min();
        var commitBody = grain[commitIdx..endCandidates];

        Assert.DoesNotContain("ex.Message.Contains(", commitBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message.Contains(", commitBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowProfileManager_ResolutionFailureSites_ThrowTypedException()
    {
        var profileManager = ReadEmbeddedFile(WorkflowServicesDir + "WorkflowProfileManager.cs");

        var typedThrows = Regex.Matches(
            profileManager,
            @"throw new WorkflowDefinitionResolutionException\(",
            RegexOptions.ExplicitCapture).Count;

        Assert.True(
            typedThrows >= 3,
            $"WorkflowProfileManager must throw WorkflowDefinitionResolutionException at all three "
            + $"resolution-failure sites; found {typedThrows}.");
    }

    [Fact]
    public void CommitAsync_HasCatchOnTypedExceptionOnly()
    {
        var grain = ReadEmbeddedFile(WorkflowGrainsDir + "WorkflowGrain.cs");

        var commitIdx = grain.IndexOf("private async Task CommitAsync(", StringComparison.Ordinal);
        Assert.True(commitIdx >= 0, "WorkflowGrain.cs must contain a CommitAsync method");

        var endCandidates = new[]
        {
            grain.IndexOf("\n    private ", commitIdx + 1, StringComparison.Ordinal),
            grain.IndexOf("\n    internal ", commitIdx + 1, StringComparison.Ordinal),
            grain.IndexOf("\n    public ", commitIdx + 1, StringComparison.Ordinal),
        }.Where(i => i > 0).DefaultIfEmpty(grain.Length).Min();
        var commitBody = grain[commitIdx..endCandidates];

        var catchTyped = commitBody.Contains(
            "catch (WorkflowDefinitionResolutionException",
            StringComparison.Ordinal);
        var catchIoe = Regex.IsMatch(
            commitBody,
            @"catch\s*\(\s*InvalidOperationException",
            RegexOptions.ExplicitCapture);

        Assert.True(catchTyped, "CommitAsync must catch WorkflowDefinitionResolutionException");
        Assert.False(
            catchIoe,
            "CommitAsync must not catch InvalidOperationException (use the typed resolution exception instead)");
    }

    private static string ReadEmbeddedFile(string relativePath)
    {
        var assembly = typeof(WorkflowGrainContractRules).Assembly;
        var fullName = "ServerSources/" + relativePath;
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded source not found: {fullName}. Available: "
                + string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertSourceHasNoDispatchEventDeclaration(string source)
    {
        Assert.DoesNotContain("DispatchEvent(", source, StringComparison.Ordinal);
    }
}
