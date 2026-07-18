using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

/// <summary>
/// issue-417 T-006 (D4) — authoritative routing overlay: the
/// overlay replaces the entire <c>repository</c> and
/// <c>workspace</c> roots from the run's persisted snapshot.
/// Configurable variables attempting to redirect those roots
/// must lose.
/// </summary>
public class AuthoritativeRoutingOverlayTests
{
    [Fact]
    public void Build_WithRepositoryAndWorkspace_PopulatesOverlayRoots()
    {
        var repository = new Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext(
            Name: "web",
            GitUrl: "git@web.example:repo.git",
            BaseBranch: "develop",
            RemoteFingerprint: "abc",
            RemoteIdentityVersion: "git-remote-url/v1");
        var workspace = new Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity(
            Path: "/run/workspaces/wr_x",
            Branch: "mohist/run-wr_x",
            ChangeDir: "/openspec/changes/issue-417");

        var overlay = AuthoritativeRoutingOverlay.Build(
            "wr_x",
            repository,
            workspace,
            projectId: "proj_y",
            issueNumber: 5);

        var element = overlay.Vars!.Value;
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            element.GetRawText(), JSON.Options)!;

        Assert.Equal("wr_x", root["mohist"].GetProperty("runId").GetString());

        var repo = root["repository"];
        Assert.Equal("web", repo.GetProperty("name").GetString());
        Assert.Equal("git@web.example:repo.git", repo.GetProperty("gitUrl").GetString());
        Assert.Equal("develop", repo.GetProperty("baseBranch").GetString());
        Assert.Equal("abc", repo.GetProperty("remoteFingerprint").GetString());
        Assert.Equal("git-remote-url/v1", repo.GetProperty("remoteIdentityVersion").GetString());

        var ws = root["workspace"];
        Assert.Equal("/run/workspaces/wr_x", ws.GetProperty("path").GetString());
        Assert.Equal("mohist/run-wr_x", ws.GetProperty("branch").GetString());
        Assert.Equal("/openspec/changes/issue-417", ws.GetProperty("changeDir").GetString());

        Assert.Equal("proj_y", root["project"].GetProperty("id").GetString());
        Assert.Equal(5, root["issue"].GetProperty("number").GetInt32());
    }

    [Fact]
    public void Build_WithoutRepository_LeavesRepositoryRootEmpty()
    {
        // Generic / non-Issue-backed run starts without a
        // repository context; the overlay must not emit a
        // misleading `repository` root.
        var overlay = AuthoritativeRoutingOverlay.Build(
            "wr_x",
            repository: null,
            workspace: null,
            projectId: null,
            issueNumber: null);

        var element = overlay.Vars!.Value;
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            element.GetRawText(), JSON.Options)!;

        Assert.Equal("wr_x", root["mohist"].GetProperty("runId").GetString());
        Assert.False(root.ContainsKey("repository"),
            "Generic runs must not advertise a repository root.");
        Assert.False(root.ContainsKey("workspace"),
            "Generic runs must not advertise a workspace root.");
    }

    [Fact]
    public void Patch_OverlayReplacesConfigurableRepository()
    {
        // The D4 contract is that no configurable layer can
        // redirect repository / workspace routing. The overlay
        // is applied last; even if a configurable layer tried
        // to set repository = "rogue", the overlay replaces it.
        var overlayRepository = new Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext(
            Name: "web",
            GitUrl: "git@web.example:repo.git",
            BaseBranch: "develop",
            RemoteFingerprint: "fingerprint-1",
            RemoteIdentityVersion: "git-remote-url/v1");

        var overlay = AuthoritativeRoutingOverlay.Build(
            "wr_x",
            overlayRepository,
            workspace: null,
            projectId: null,
            issueNumber: null);

        // A "rogue" configurable layer that sets a different
        // repository. VariableBundle.Patch applies overlay
        // last so it must replace the rogue value.
        var rogueBundle = new VariableBundle(
            Vars: JSON.DeserializeElement("""
                {
                  "mohist": { "runId": "sentinel" },
                  "repository": {
                    "name": "rogue",
                    "gitUrl": "git@rogue.example:rogue.git",
                    "baseBranch": "main",
                    "remoteFingerprint": "rogue",
                    "remoteIdentityVersion": "rogue/v1"
                  }
                }
                """));

        var merged = VariableBundle.Patch(rogueBundle, overlay);

        var root = merged.Vars!.Value;
        var repository = root.GetProperty("repository");

        Assert.Equal("web", repository.GetProperty("name").GetString());
        Assert.Equal("fingerprint-1", repository.GetProperty("remoteFingerprint").GetString());
        Assert.Equal("git@web.example:repo.git", repository.GetProperty("gitUrl").GetString());

        // The authoritative runId replaces the configurable one.
        var mohist = root.GetProperty("mohist");
        Assert.Equal("wr_x", mohist.GetProperty("runId").GetString());
    }

    [Fact]
    public void Apply_AfterStageMerge_ReplacesCompleteRoutingRoots()
    {
        var repository = new Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext(
            "web", "git@web.example:repo.git", "develop", "fingerprint-1", "git-remote-url/v1");
        var workspace = new Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity(
            "/run/workspaces/wr_x", "mohist/run-wr_x", "/openspec/changes/issue-417");
        var layered = JSON.DeserializeElement("""
            {
              "repository": { "name": "rogue", "token": "must-disappear" },
              "workspace": { "path": "/rogue", "branch": "rogue" },
              "mohist": { "runId": "wrong" },
              "project": { "id": "wrong", "name": "retain" },
              "issue": { "number": 99 }
            }
            """);

        var result = AuthoritativeRoutingOverlay.Apply(
            layered,
            "wr_x",
            repository,
            workspace,
            "proj_y",
            5);

        var root = result.Vars!.Value;
        Assert.Equal("web", root.GetProperty("repository").GetProperty("name").GetString());
        Assert.False(root.GetProperty("repository").TryGetProperty("token", out _));
        Assert.Equal("/run/workspaces/wr_x", root.GetProperty("workspace").GetProperty("path").GetString());
        Assert.Equal("wr_x", root.GetProperty("mohist").GetProperty("runId").GetString());
        Assert.Equal("proj_y", root.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(5, root.GetProperty("issue").GetProperty("number").GetInt32());
    }
}
