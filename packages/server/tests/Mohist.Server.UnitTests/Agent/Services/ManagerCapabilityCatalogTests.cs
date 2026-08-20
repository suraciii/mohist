using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class ManagerCapabilityCatalogTests
{
    [Fact]
    public void Management_catalog_contains_only_supported_operations()
    {
        Assert.Equal(
            [
                ManagerCapabilityCatalog.AgentCreateOrMount,
                ManagerCapabilityCatalog.AgentList,
                ManagerCapabilityCatalog.AgentView,
                ManagerCapabilityCatalog.ConnectionAccessPolicy,
                ManagerCapabilityCatalog.ConnectionDiagnostics,
                ManagerCapabilityCatalog.ConnectionDisable,
                ManagerCapabilityCatalog.ConnectionEnable,
                ManagerCapabilityCatalog.ConnectionList,
                ManagerCapabilityCatalog.ConnectionView,
                ManagerCapabilityCatalog.OwnerClaim,
                ManagerCapabilityCatalog.OwnerTransfer,
                ManagerCapabilityCatalog.WorkspaceStatus,
            ],
            ManagerCapabilityCatalog.ManagementCapabilities.OrderBy(value => value).ToArray());
    }

    [Theory]
    [InlineData("slack", "status", ManagerCapabilityCatalog.WorkspaceStatus)]
    [InlineData("slack", "list", "--workspace-team", ManagerCapabilityCatalog.AgentList)]
    [InlineData("slack", "list", ManagerCapabilityCatalog.ConnectionList)]
    [InlineData("slack", "view", ManagerCapabilityCatalog.ConnectionDiagnostics)]
    [InlineData("slack", "diagnostics", ManagerCapabilityCatalog.ConnectionDiagnostics)]
    [InlineData("slack", "create", ManagerCapabilityCatalog.AgentCreateOrMount)]
    [InlineData("slack", "edit", "--access-policy", ManagerCapabilityCatalog.ConnectionAccessPolicy)]
    [InlineData("slack", "enable", ManagerCapabilityCatalog.ConnectionEnable)]
    [InlineData("slack", "disable", ManagerCapabilityCatalog.ConnectionDisable)]
    [InlineData("slack", "claim-owner", ManagerCapabilityCatalog.OwnerClaim)]
    [InlineData("slack", "transfer-owner", ManagerCapabilityCatalog.OwnerTransfer)]
    [InlineData("agent", "list", ManagerCapabilityCatalog.AgentList)]
    [InlineData("agent", "view", ManagerCapabilityCatalog.AgentView)]
    [InlineData("agent", "create", ManagerCapabilityCatalog.AgentCreateOrMount)]
    public void Cli_forms_resolve_to_one_catalog_capability(params string[] args)
    {
        Assert.Equal(args[^1], ManagerCapabilityCatalog.ResolveCli(args[..^1]));
    }

    [Theory]
    [InlineData("POST", "/api/projects/proj/slack-manager/connections/connection/remove-binding")]
    [InlineData("POST", "/api/projects/proj/slack-manager/connections/connection/permanent-delete")]
    [InlineData("POST", "/api/projects/proj/slack-manager/install-agent/credentials")]
    [InlineData("POST", "/api/projects/proj/slack-manager/setup/runtime-credentials")]
    [InlineData("GET", "/api/v1/projects/proj/agents")]
    public void Protected_or_arbitrary_http_routes_are_not_catalogued(string method, string path)
    {
        Assert.Null(ManagerCapabilityCatalog.ResolveHttp(method, path));
    }

    [Theory]
    [InlineData("GET", "/api/slack-manager/status", ManagerCapabilityCatalog.WorkspaceStatus)]
    [InlineData("GET", "/api/projects/proj/slack-manager/agents", ManagerCapabilityCatalog.AgentList)]
    [InlineData("GET", "/api/projects/proj/slack-manager/connections/connection", ManagerCapabilityCatalog.ConnectionView)]
    [InlineData("POST", "/api/projects/proj/slack-manager/apps", ManagerCapabilityCatalog.AgentCreateOrMount)]
    [InlineData("GET", "/api/projects/proj/agents", ManagerCapabilityCatalog.AgentList)]
    [InlineData("POST", "/api/projects/proj/agents", ManagerCapabilityCatalog.AgentCreateOrMount)]
    [InlineData("GET", "/api/projects/proj/slack-connections", ManagerCapabilityCatalog.ConnectionList)]
    [InlineData("GET", "/api/projects/proj/slack-connections/connection/diagnostic", ManagerCapabilityCatalog.ConnectionDiagnostics)]
    [InlineData("POST", "/api/projects/proj/slack-connections/connection/manage-access", ManagerCapabilityCatalog.ConnectionAccessPolicy)]
    [InlineData("POST", "/api/projects/proj/slack-connections/connection/enable", ManagerCapabilityCatalog.ConnectionEnable)]
    [InlineData("POST", "/api/projects/proj/slack-connections/connection/disable", ManagerCapabilityCatalog.ConnectionDisable)]
    [InlineData("POST", "/api/projects/proj/slack-connections/connection/transfer-owner", ManagerCapabilityCatalog.OwnerTransfer)]
    public void Supported_http_routes_resolve_to_one_catalog_capability(string method, string path, string expected)
    {
        Assert.Equal(expected, ManagerCapabilityCatalog.ResolveHttp(method, path));
    }
}
