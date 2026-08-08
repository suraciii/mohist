using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// <c>mo audit list</c>: fetches the auth audit trail from
/// <c>/api/audit/events</c>, passes kind/since/limit filters through as
/// query parameters and renders rows (never token values — the server
/// trail carries none).
/// </summary>
public sealed class CliAuditCommandSpecs
{
    [Fact]
    public async Task List_SendsTheRequest_AndRendersRows()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    events = new[]
                    {
                        new
                        {
                            id = "audit_1",
                            subjectId = "admin",
                            eventType = "credentialIssued",
                            targetKind = "credential",
                            targetId = "pat_1",
                            occurredAt = "2026-08-22T00:00:00+00:00",
                            metadata = new { kind = "pat", name = "ci" },
                        },
                    },
                },
            })));

        var exitCode = await RunAsync(http, ["audit", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/audit/events", request.RequestUri!.AbsolutePath);
        Assert.Equal("", request.RequestUri.Query);

        var stdout = output.ToString();
        Assert.Contains("credentialIssued", stdout);
        Assert.Contains("admin", stdout);
        Assert.Contains("pat_1", stdout);
        Assert.Contains("kind=pat", stdout);
        Assert.Contains("name=ci", stdout);
        Assert.Contains("2026-08-22T00:00:00+00:00", stdout);
    }

    [Fact]
    public async Task List_WithFilters_BuildsTheQueryString()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { events = new JsonArray() },
            })));

        var exitCode = await RunAsync(
            http,
            ["audit", "list", "--kind", "credentialIssued", "--since", "2026-08-01T00:00:00Z", "--limit", "5"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/audit/events", request.RequestUri!.AbsolutePath);
        var query = request.RequestUri!.Query;
        Assert.Contains("kind=credentialIssued", query);
        Assert.Contains("since=2026-08-01T00%3A00%3A00Z", query);
        Assert.Contains("limit=5", query);
    }

    [Fact]
    public async Task List_Empty_PrintsNoAuditEvents()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { events = new JsonArray() },
            })));

        var exitCode = await RunAsync(http, ["audit", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No audit events", output.ToString());
    }

    private static Task<int> RunAsync(
        HttpClient http,
        string[] args,
        StringWriter output,
        StringWriter error,
        FakeFileSystem fs,
        FakeCommandExecutor executor)
    {
        return MohistCliCommands.RunAsync(http, args, output, error, fs, executor);
    }
}
