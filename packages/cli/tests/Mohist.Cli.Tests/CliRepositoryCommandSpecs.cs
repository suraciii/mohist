using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRepositoryCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    public static IEnumerable<object[]> RepoProjectScopeCases()
    {
        yield return [new[] { "repo", "list", "--project", "proj_by_name" }, HttpMethod.Get, "/api/projects/proj_by_name/repositories"];
        yield return [new[] { "repo", "list", "--project-id", "proj_by_id" }, HttpMethod.Get, "/api/projects/proj_by_id/repositories"];
        yield return [new[] { "repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--project", "proj_by_name" }, HttpMethod.Post, "/api/projects/proj_by_name/repositories"];
        yield return [new[] { "repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--project-id", "proj_by_id" }, HttpMethod.Post, "/api/projects/proj_by_id/repositories"];
        yield return [new[] { "repo", "update", "origin", "--new-name", "upstream", "--project", "proj_by_name" }, HttpMethod.Patch, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "update", "origin", "--new-name", "upstream", "--project-id", "proj_by_id" }, HttpMethod.Patch, "/api/projects/proj_by_id/repositories/origin"];
        yield return [new[] { "repo", "set-default", "origin", "--project", "proj_by_name" }, HttpMethod.Patch, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "set-default", "origin", "--project-id", "proj_by_id" }, HttpMethod.Patch, "/api/projects/proj_by_id/repositories/origin"];
        yield return [new[] { "repo", "delete", "origin", "--project", "proj_by_name" }, HttpMethod.Delete, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "delete", "origin", "--project-id", "proj_by_id" }, HttpMethod.Delete, "/api/projects/proj_by_id/repositories/origin"];
    }

    private static (RecordingHttpHandler handler, HttpClient http, StringWriter output, StringWriter error, FakeFileSystem fs, FakeCommandExecutor executor)
        SetupEnv(
            Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
            string? activeProjectId = ActiveProjectId)
    {
        return CliTestFactory.CreateSync(req =>
        {
            var response = responder?.Invoke(req);
            return response ?? RecordingHttpHandler.Json(new { success = true, data = new { } });
        }, activeProjectId);
    }

    [Fact]
    public async Task RepoHelp_ListsTheFiveSubcommands()
    {
        var (_, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "--help"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("repo [command] [options]", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("repository [command] [options]", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("add", stdout, StringComparison.Ordinal);
        Assert.Contains("update", stdout, StringComparison.Ordinal);
        Assert.Contains("set-default", stdout, StringComparison.Ordinal);
        Assert.Contains("delete", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryAlias_RemainsAccepted()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repository", "list"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProjectRepo_AnySubcommand_IsRejectedAsUnrecognized()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "repo", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProjectRepo_Add_IsRejectedAsUnrecognized()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "repo", "add", "origin", "--git-url", "git@example.com:repo.git"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoAdd_WithGitUrl_SendsRepositoryMetadataWithNoPathFields()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--base-branch", "main", "--set-default"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("origin", body!["name"]?.GetValue<string>());
        Assert.Equal("git@example.com:repo.git", body["gitUrl"]?.GetValue<string>());
        Assert.Equal("main", body["baseBranch"]?.GetValue<string>());
        Assert.True(body["isDefault"]?.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("remote"));
        Assert.False(body.AsObject().ContainsKey("resolvedPath"));
    }

    [Fact]
    public async Task RepoAdd_WithProjectFlag_SendsToResolvedProject()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--base-branch", "main", "--set-default", "--project", "proj_by_name"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_by_name/repositories", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoAdd_WithoutGitUrl_IsRejectedWithClearError()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("git-url", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoAdd_WithDroppedDefaultFlag_IsRejected()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--default"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoUpdate_WithAllFlags_SendsPatchWithOnlySuppliedFields()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "origin", "--new-name", "upstream", "--git-url", "git@example.com:repo-v2.git", "--base-branch", "develop"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("upstream", body["newName"]?.GetValue<string>());
        Assert.Equal("git@example.com:repo-v2.git", body["gitUrl"]?.GetValue<string>());
        Assert.Equal("develop", body["baseBranch"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("setDefault"));
    }

    [Fact]
    public async Task RepoUpdate_WithSetDefault_SendsSetDefaultTrue()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "origin", "--set-default"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.True(body["setDefault"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RepoSetDefault_SendsPatchWithSetDefaultTrue()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "set-default", "origin"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!)!;
        Assert.True(body["setDefault"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RepoDelete_SendsDelete()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "delete", "origin"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoRemove_AliasesDelete()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "remove", "origin"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoRm_AliasesDelete()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "rm", "origin"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoList_AcceptsProjectFlag()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "--project", "proj_by_name"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_by_name/repositories", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoList_AcceptsProjectIdAlias()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "--project-id", "proj_by_id"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_by_id/repositories", request.RequestUri?.PathAndQuery);
    }

    [Theory]
    [MemberData(nameof(RepoProjectScopeCases))]
    public async Task RepoSubcommands_AcceptProjectAndProjectId(string[] args, HttpMethod expectedMethod, string expectedPath)
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            args,
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(expectedMethod, request.Method);
        Assert.Equal(expectedPath, request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoList_NoResolvableProject_FailsClearlyWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("mo project use", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoAdd_NoResolvableProject_FailsClearlyWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("mo project use", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoList_TableMode_RendersRepoListTable()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "main", isDefault = true },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "-o", "table"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("origin", stdout, StringComparison.Ordinal);
        Assert.Contains("main", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoList_JsonMode_PrintsRawServerPayload()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "main", isDefault = true },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "-o", "json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("origin", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoAdd_JsonMode_SendsPostAndEmitsRawServerPayload()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { name = "origin", gitUrl = "git@example.com:repo.git" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "-o", "json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        var stdout = output.ToString();
        Assert.Contains("\"name\": \"origin\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoAdd_InvalidOutput_FailsWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "-o", "yaml"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("table", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("json", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
