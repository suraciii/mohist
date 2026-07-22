using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueRejectAndStopSpecs
{
    [Fact]
    public async Task IssueReject_SuccessPath_SendsPostWithMessageAndPrintsConfirmation()
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
                        id = "issue_42",
                        number = 42,
                        title = "T",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "reject", "42", "--message", "Rework the auth flow"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/reject", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("Rework the auth flow", body["message"]?.GetValue<string>());
        var stdout = output.ToString();
        Assert.DoesNotContain("error", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueReject_MissingMessage_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "reject", "42"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--message", stderr);
    }

    [Fact]
    public async Task IssueReject_EmptyMessage_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "reject", "42", "--message", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--message", stderr);
    }

    [Fact]
    public async Task IssueReject_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_42", number = 42, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "reject", "42", "--message", "rework", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/reject", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueStop_SuccessPath_SendsPostAndPrintsConfirmation()
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
                        id = "issue_42",
                        number = 42,
                        title = "T",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "stop", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/stop", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueStop_HelpExplainsTerminalAndDistinguishesFromForceStop()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "stop", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("stop", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminal", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("force-stop", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueStop_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_42", number = 42, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "stop", "42", "--project", "proj_xyz"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/stop", postReq.RequestUri?.PathAndQuery);
    }
}
