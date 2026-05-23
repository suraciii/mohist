using System.Text.Json;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class WorkspaceManagerSpecs
{
    [Fact]
    public async Task UsesExistingWorkspaceVariable()
    {
        using var temp = new TempDir();
        var workspacePath = Path.Combine(temp.Path, "existing-workspace");
        Directory.CreateDirectory(workspacePath);

        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = new Dictionary<string, JsonElement?>
        {
            ["workspace"] = JsonSerializer.SerializeToElement(new { path = workspacePath })
        };

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Equal(workspacePath, info.Path);
    }

    [Fact]
    public async Task CreatesFallbackWorkspace()
    {
        using var temp = new TempDir();
        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
        {"project":{"id":"test"},"issue":{"number":42}}
        """)!;

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Contains("issue-42", info.Path);
        Assert.True(Directory.Exists(info.Path));
    }

    [Fact]
    public async Task CreatesChangeDirInWorkspace()
    {
        using var temp = new TempDir();
        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
        {"project":{"id":"test"},"issue":{"number":42},"artifacts":{"changeDir":"openspec/changes/42-search"}}
        """)!;

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.NotNull(info.ChangeDir);
        Assert.EndsWith(Path.Combine("openspec", "changes", "42-search"), info.ChangeDir);
        Assert.True(Directory.Exists(info.ChangeDir));
        Assert.True(Directory.Exists(Path.Combine(info.ChangeDir, "specs")));
    }
}
