using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssuePrereqSpecs
{
    [Fact]
    public async Task IssuePrereqAdd_SuccessPath_SendsPostWithPrerequisiteNumber()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        prerequisiteNumbers = new[] { 200 },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "add", "201", "200"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/201/prerequisites", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal(200, body["prerequisiteNumber"]?.GetValue<int>());
    }

    [Fact]
    public async Task IssuePrereqAdd_CircularDependency_SurfacesServerErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Issue cannot depend on itself", code = "circular_prerequisite" },
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "add", "200", "201"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("circular", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Issue cannot depend on itself", stderr);
    }

    [Fact]
    public async Task IssuePrereqAdd_NonexistentPrerequisiteIssue_SurfacesServerError()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Issue #9999 not found", code = "prerequisite_not_found" },
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "add", "42", "9999"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Issue #9999 not found", stderr);
    }

    [Fact]
    public async Task IssuePrereqAdd_NonexistentPrerequisiteIssue_DoesNotReportSilentSuccess()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Issue #9999 not found", code = "prerequisite_not_found" },
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "add", "42", "9999"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("OK", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"success\": true", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssuePrereqRemove_SuccessPath_SendsDeleteToPrereqEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        prerequisiteNumbers = Array.Empty<int>(),
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "remove", "201", "200"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Last(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/201/prerequisites/200", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssuePrereq_HelpListsAddAndRemoveSubcommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "prereq", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("add", stdout);
        Assert.Contains("remove", stdout);
    }

    [Fact]
    public async Task IssuePrereqAdd_AcceptsProjectReferenceFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_201", number = 201, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "prereq", "add", "201", "200", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/201/prerequisites", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssuePrereqRemove_AcceptsProjectReferenceFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_201", number = 201, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "prereq", "remove", "201", "200", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Last(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_xyz/issues/201/prerequisites/200", deleteReq.RequestUri?.PathAndQuery);
    }
}
