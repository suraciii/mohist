using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliProjectCommandSpecs
{
    private const string RepoRoot = "/work/product-a";

    [Fact]
    public async Task ProjectCreate_WithValidGitPath_PostsRepositoryBackedCreation()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "proj_123",
                    name = "my-project",
                    repositories = new[]
                    {
                        new { name = "product-a", gitUrl = "git@example.com:team/product-a.git", baseBranch = "main", isDefault = true },
                    },
                },
            }, HttpStatusCode.Created),
            activeProjectId: null);

        SeedGitRepo(fileSystem, executor, RepoRoot, gitUrl: "git@example.com:team/product-a.git", baseBranch: "main");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("my-project", body["name"]?.GetValue<string>());
        var repo = body["repository"];
        Assert.NotNull(repo);
        Assert.Equal("product-a", repo!["name"]?.GetValue<string>());
        Assert.Equal("git@example.com:team/product-a.git", repo["gitUrl"]?.GetValue<string>());
        Assert.Equal("main", repo["baseBranch"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("effectivePath"));
        Assert.False(body.AsObject().ContainsKey("workTreeRoot"));
        Assert.False(body.AsObject().ContainsKey("remote"));
        Assert.False(body.AsObject().ContainsKey("origin"));
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_WithNestedGitPath_PostsRepositoryNamedAfterResolvedRoot()
    {
        var nestedPath = Path.Combine(RepoRoot, "src");
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_123", name = "my-project" },
            }, HttpStatusCode.Created),
            activeProjectId: null);
        fileSystem.CreateDirectory(RepoRoot);
        SeedGitRepo(
            fileSystem,
            executor,
            nestedPath,
            resolvedWorkTreeRoot: RepoRoot,
            createGitMarker: false);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", nestedPath], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("product-a", body["repository"]!["name"]?.GetValue<string>());
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_WithoutPath_RejectsBeforeAnyRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_123", name = "my-project" },
            }, HttpStatusCode.Created),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--path", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectCreate_PathNotAGitRepo_RejectsBeforeAnyRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new { success = true, data = new { } }, HttpStatusCode.Created),
            activeProjectId: null);

        fileSystem.CreateDirectory(RepoRoot);
        QueueGit(executor, RepoRoot, ["rev-parse", "--show-toplevel"], 128, "", "fatal: not a git repository");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(RepoRoot, error.ToString(), StringComparison.Ordinal);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_PathMissingOrigin_RejectsBeforeAnyRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new { success = true, data = new { } }, HttpStatusCode.Created),
            activeProjectId: null);

        SeedGitRepo(fileSystem, executor, RepoRoot, includeOrigin: false, includeBranch: true, branch: "main");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("origin", error.ToString(), StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_RunnerLocalOrigin_PostsRepositoryDeclaration()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new { success = true, data = new { } }, HttpStatusCode.Created),
            activeProjectId: null);

        fileSystem.CreateDirectory(RepoRoot);
        QueueGit(executor, RepoRoot, ["rev-parse", "--show-toplevel"], 0, RepoRoot + "\n");
        QueueGit(executor, RepoRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, RepoRoot, ["remote", "get-url", "origin"], 0, "file:///srv/remote.git\n");
        QueueGit(executor, RepoRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "origin/main\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("file:///srv/remote.git", body["repository"]!["gitUrl"]?.GetValue<string>());
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_PathHasNoResolvableBranch_RejectsBeforeAnyRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new { success = true, data = new { } }, HttpStatusCode.Created),
            activeProjectId: null);

        SeedGitRepo(fileSystem, executor, RepoRoot, includeOrigin: true, includeOriginHead: false, includeBranch: false, gitUrl: "git@example.com:team/product-a.git");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("branch", stderr, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectCreate_OriginHeadFallsBackToCheckedOutBranch()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_123", name = "my-project" },
            }, HttpStatusCode.Created),
            activeProjectId: null);

        SeedGitRepo(fileSystem, executor, RepoRoot, gitUrl: "git@example.com:team/product-a.git", includeOrigin: true, includeOriginHead: false, includeBranch: true, branch: "develop");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project", "--path", RepoRoot], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("develop", body["repository"]!["baseBranch"]?.GetValue<string>());
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task ProjectList_DisplaysNamesAndCurrentMarkerWithoutPaths()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "proj_a", name = "alpha" },
                    new { id = "proj_b", name = "beta" },
                },
            }),
            "proj_b");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "list",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("  alpha", lines[0]);
        Assert.Equal("* beta", lines[1]);
        Assert.DoesNotContain("path", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void SeedGitRepo(
        FakeFileSystem fileSystem,
        FakeCommandExecutor executor,
        string workTreeRoot,
        string gitUrl = "git@example.com:team/product-a.git",
        string baseBranch = "main",
        bool includeOrigin = true,
        bool includeOriginHead = true,
        bool includeBranch = true,
        string? branch = null,
        string? resolvedWorkTreeRoot = null,
        bool createGitMarker = true)
    {
        var resolvedRoot = resolvedWorkTreeRoot ?? workTreeRoot;
        fileSystem.CreateDirectory(workTreeRoot);
        if (createGitMarker)
            fileSystem.CreateDirectory(Path.Combine(workTreeRoot, ".git"));

        executor.Invocations.Clear();
        QueueGit(executor, workTreeRoot, ["rev-parse", "--show-toplevel"], 0, resolvedRoot + "\n");
        QueueGit(executor, resolvedRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        if (includeOrigin)
        {
            QueueGit(executor, resolvedRoot, ["remote", "get-url", "origin"], 0, gitUrl + "\n");
        }
        else
        {
            QueueGit(executor, resolvedRoot, ["remote", "get-url", "origin"], 128, "", "fatal: No such remote 'origin'\n");
            return;
        }

        if (includeOriginHead)
        {
            QueueGit(executor, resolvedRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, $"origin/{baseBranch}\n");
            return;
        }

        QueueGit(executor, resolvedRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 1, "", "fatal: no origin/HEAD\n");
        if (includeBranch)
        {
            QueueGit(executor, resolvedRoot, ["symbolic-ref", "--short", "HEAD"], 0, $"{(branch ?? baseBranch)}\n");
        }
        else
        {
            QueueGit(executor, resolvedRoot, ["symbolic-ref", "--short", "HEAD"], 1, "", "fatal: not a symbolic ref\n");
        }
    }

    private static void QueueGit(
        FakeCommandExecutor executor,
        string workingDirectory,
        string[] gitArgs,
        int exitCode,
        string stdout = "",
        string stderr = "")
    {
        var args = new List<string> { "-C", workingDirectory };
        args.AddRange(gitArgs);
        executor.QueueExpected("git", args.ToArray(), workingDirectory, exitCode, stdout, stderr);
    }
}
