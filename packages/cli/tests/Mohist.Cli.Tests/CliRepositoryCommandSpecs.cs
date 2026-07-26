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
        yield return [new[] { "repo", "list", "--project", "proj_by_id" }, HttpMethod.Get, "/api/projects/proj_by_id/repositories"];
        yield return [new[] { "repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--project", "proj_by_name" }, HttpMethod.Post, "/api/projects/proj_by_name/repositories"];
        yield return [new[] { "repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--project", "proj_by_id" }, HttpMethod.Post, "/api/projects/proj_by_id/repositories"];
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "develop", "--project", "proj_by_name" }, HttpMethod.Patch, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "develop", "--project", "proj_by_id" }, HttpMethod.Patch, "/api/projects/proj_by_id/repositories/origin"];
        yield return [new[] { "repo", "set-default", "origin", "--project", "proj_by_name" }, HttpMethod.Patch, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "set-default", "origin", "--project", "proj_by_id" }, HttpMethod.Patch, "/api/projects/proj_by_id/repositories/origin"];
        yield return [new[] { "repo", "delete", "origin", "--project", "proj_by_name" }, HttpMethod.Delete, "/api/projects/proj_by_name/repositories/origin"];
        yield return [new[] { "repo", "delete", "origin", "--project", "proj_by_id" }, HttpMethod.Delete, "/api/projects/proj_by_id/repositories/origin"];
    }

    public static IEnumerable<object[]> SingleFieldUpdateCases()
    {
        yield return [new[] { "repo", "edit", "origin", "--git-url", "git@example.com:repo-v2.git" }, "gitUrl", "git@example.com:repo-v2.git"];
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "release" }, "baseBranch", "release"];
    }

    public static IEnumerable<object[]> NoResolvableProjectCases()
    {
        yield return [new[] { "repo", "list" }];
        yield return [new[] { "repo", "add", "origin", "--git-url", "git@example.com:repo.git" }];
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "release" }];
        yield return [new[] { "repo", "set-default", "origin" }];
        yield return [new[] { "repo", "delete", "origin" }];
    }

    public static IEnumerable<object[]> MutationOutputCases()
    {
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "release", }, false];
        yield return [new[] { "repo", "edit", "origin", "--base-branch", "release", "--json", "name,isDefault" }, true];
        yield return [new[] { "repo", "set-default", "origin", }, false];
        yield return [new[] { "repo", "set-default", "origin", "--json", "name,isDefault" }, true];
        yield return [new[] { "repo", "delete", "origin", }, false];
        yield return [new[] { "repo", "delete", "origin", "--json", "name,isDefault" }, true];
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
        Assert.Contains("USAGE", stdout, StringComparison.Ordinal);
        Assert.Contains("mo repo", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("repository [command]", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("add", stdout, StringComparison.Ordinal);
        Assert.Contains("edit", stdout, StringComparison.Ordinal);
        Assert.Contains("set-default", stdout, StringComparison.Ordinal);
        Assert.Contains("delete", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("update", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("remove", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(" rm ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(" ls ", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryAlias_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repository", "list"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
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
        Assert.False(body.AsObject().ContainsKey("setDefault"));
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
            ["repo", "edit", "origin", "--git-url", "git@example.com:repo-v2.git", "--base-branch", "develop"],
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

    [Theory]
    [MemberData(nameof(SingleFieldUpdateCases))]
    public async Task RepoUpdate_WithOneMetadataField_SendsOnlyThatField(
        string[] args,
        string expectedField,
        string expectedValue)
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Single(body);
        Assert.Equal(expectedValue, body[expectedField]?.GetValue<string>());
    }

    [Fact]
    public async Task RepoUpdate_WithoutMetadata_IsRejectedWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "edit", "origin"],
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
        var args = new List<string> { "repo", "edit", "origin", option };
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
            ["repo", "set-default", "origin",],
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
                "Repository 'origin' is the default. Run 'mo repo set-default <other-name>' first.",
                "repository_default_deletion_conflict",
                HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "delete", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("origin", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo repo set-default", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoDelete_InUseRepository_SurfacesConflictWithoutSuccessOutput()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Repository 'web' is referenced by one or more non-terminal issues and cannot be removed",
                "repository_in_use",
                HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "delete", "web"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("Repository 'web'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("non-terminal", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deleted", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepoDelete_MissingRepository_RetainsNotFoundMeaning()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.JsonError(
                "Repository 'missing' was not found in Project 'proj_test'.",
                "repository_not_found",
                HttpStatusCode.NotFound));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "delete", "missing"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("missing", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("deleted", output.ToString(), StringComparison.OrdinalIgnoreCase);
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
            ["repo", "edit", "missing", "--base-branch", "release"],
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
    public async Task RepoRemove_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "remove", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoRm_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "rm", "origin"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoLs_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "ls"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepoUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "update", "origin", "--base-branch", "release"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
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
            ["repo", "list", "--project", "proj_by_id"],
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

    [Theory]
    [MemberData(nameof(NoResolvableProjectCases))]
    public async Task RepoSubcommand_NoResolvableProject_FailsClearlyWithoutDispatch(string[] args)
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

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
            ["repo", "list",],
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
    public async Task RepoList_SelectedJson_ProjectsRequestedFields()
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
            ["repo", "list", "--json", "name,gitUrl,baseBranch,isDefault"],
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
    public async Task RepoAdd_SelectedJson_SendsPostAndEmitsRepositoryCollection()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        repositories = new[]
                        {
                            new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "main", isDefault = true },
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--json", "name,gitUrl"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        var stdout = output.ToString();
        var repositories = JsonNode.Parse(stdout) as JsonArray;
        Assert.NotNull(repositories);
        Assert.Equal("origin", repositories![0]!["name"]?.GetValue<string>());
        Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MutationOutputCases))]
    public async Task RepoMutations_RenderRepositoryStateForEveryOutputMode(string[] args, bool jsonOutput)
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    repositories = new[]
                    {
                        new { name = "server", gitUrl = "git@example.com:server.git", baseBranch = "main", isDefault = false },
                        new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "release", isDefault = true },
                    },
                },
            }));

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var stdout = output.ToString();
        if (jsonOutput)
        {
            var repositories = JsonNode.Parse(stdout) as JsonArray;
            Assert.NotNull(repositories);
            Assert.Equal(2, repositories!.Count);
            Assert.Equal("origin", repositories[1]!["name"]?.GetValue<string>());
            Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("git URL", stdout, StringComparison.Ordinal);
            Assert.Contains("origin", stdout, StringComparison.Ordinal);
            Assert.Contains("yes", stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RepoAdd_LegacyOutputOption_FailsWithoutDispatch()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repo", "add", "origin", "--git-url", "git@example.com:repo.git", "--output", "json"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
