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
        yield return [new[] { "repo", "update", "origin", "--base-branch", "develop", "--project", "proj_by_name" }, HttpMethod.Patch, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "update", "origin", "--base-branch", "develop", "--project-id", "proj_by_id" }, HttpMethod.Patch, "/api/projects/proj_by_id/repositories/origin"];
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
        Assert.True(body["setDefault"]?.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("remote"));
        Assert.False(body.AsObject().ContainsKey("resolvedPath"));
    }

    [Fact]
    public async Task RepoAdd_WithoutBaseBranch_SendsMainWithoutDefaultSelection()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "web", "--git-url", "git@example.com:web.git"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("main", body["baseBranch"]?.GetValue<string>());
        Assert.False(body["setDefault"]?.GetValue<bool>());
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
    public async Task RepoUpdate_WithMetadata_SendsOnlySuppliedFields()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "origin", "--git-url", "git@example.com:repo-v2.git", "--base-branch", "develop"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("git@example.com:repo-v2.git", body["gitUrl"]?.GetValue<string>());
        Assert.Equal("develop", body["baseBranch"]?.GetValue<string>());
        Assert.Equal(2, body.AsObject().Count);
    }

    [Fact]
    public async Task RepoUpdate_WithoutMetadata_IsRejectedWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Repository 'origin'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("git-url", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base-branch", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("--new-name", "upstream")]
    [InlineData("--set-default", null)]
    public async Task RepoUpdate_WithUnsupportedOption_IsRejectedWithoutDispatch(string option, string? value)
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();
        var args = new List<string> { "repo", "update", "origin", option };
        if (value is not null) args.Add(value);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            args.ToArray(),
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(option, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
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
    public async Task RepoSetDefault_CurrentDefault_SucceedsAndRendersRepositoryState()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(req =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    repositories = new[]
                    {
                        new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "main", isDefault = true },
                    },
                },
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "set-default", "origin", "--output", "table"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("origin", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("yes", output.ToString(), StringComparison.Ordinal);
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
    public async Task RepoDelete_DefaultRepository_SurfacesActionableConflict()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Repository 'origin' is the default repository for Project 'proj_test'. Run 'mo repo set-default <other-name>' first.",
                "repository_default_deletion_conflict",
                HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "delete", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("origin", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Project 'proj_test'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo repo set-default", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoAdd_DuplicateRepository_SurfacesRepositoryConflict()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Repository 'origin' is already declared for Project 'proj_test'.",
                "repository_name_conflict",
                HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("origin", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Project 'proj_test'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoUpdate_MissingRepository_SurfacesNotFound()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Repository 'missing' was not found in Project 'proj_test'.",
                "repository_not_found",
                HttpStatusCode.NotFound));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "missing", "--base-branch", "release"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("missing", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Project 'proj_test'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoList_MissingProject_SurfacesNotFound()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Project 'missing-project' was not found.",
                "project_not_found",
                HttpStatusCode.NotFound),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "--project", "missing-project"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("missing-project", error.ToString(), StringComparison.Ordinal);
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
    public async Task RepoList_TableMode_RendersGitMetadataAndDefaultStatus()
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
                        new { name = "server", gitUrl = "git@example.com:server.git", baseBranch = "main", isDefault = true },
                        new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "develop", isDefault = false },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "list", "-o", "table"],
            output, error, fs, executor);

        Assert.True(exitCode == 0, error.ToString());
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("git URL", stdout, StringComparison.Ordinal);
        Assert.Contains("server", stdout, StringComparison.Ordinal);
        Assert.Contains("git@example.com:server.git", stdout, StringComparison.Ordinal);
        Assert.Contains("web", stdout, StringComparison.Ordinal);
        Assert.Contains("git@example.com:web.git", stdout, StringComparison.Ordinal);
        Assert.Contains("yes", stdout, StringComparison.Ordinal);
        Assert.Contains("no", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("path", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote", stdout, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("\"name\": \"origin\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"gitUrl\": \"git@example.com:repo.git\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"baseBranch\": \"main\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"isDefault\": true", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoAdd_TableMode_RendersRepositoriesFromUpdatedProject()
    {
        var (_, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    repositories = new[]
                    {
                        new { name = "server", gitUrl = "git@example.com:server.git", baseBranch = "main", isDefault = false },
                        new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "develop", isDefault = true },
                    },
                },
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "web", "--git-url", "git@example.com:web.git", "--base-branch", "develop", "--set-default"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("server", stdout, StringComparison.Ordinal);
        Assert.Contains("web", stdout, StringComparison.Ordinal);
        Assert.Contains("git URL", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No projects", stdout, StringComparison.OrdinalIgnoreCase);
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
