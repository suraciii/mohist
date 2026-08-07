using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for `mo session schedule create|list|cancel` (scheduled input,
// subagents.md 定时输入). The CLI owns only the command surface, the
// RFC 3339-with-offset input gate and the follow-up idempotency-key
// convention; due-time recency and delivery semantics are Server-side and
// are exercised here through fake HTTP responses only.
public sealed class CliSessionScheduleCommandSpecs
{
    private const string ActiveProjectId = "proj_test";
    private const string StableSessionId = "sess_123";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    // ----- help / command tree -----

    [Fact]
    public async Task SessionScheduleHelp_ListsSubcommands()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("create", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("cancel", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionScheduleCreateHelp_ListsAtTextAndIdempotencyKeyFlags()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "create", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--at", stdout, StringComparison.Ordinal);
        Assert.Contains("--text", stdout, StringComparison.Ordinal);
        Assert.Contains("--idempotency-key", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionScheduleListHelp_NamesSessionArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "list", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("session-id", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ----- create: local validation -----

    [Fact]
    public async Task SessionScheduleCreate_MissingAt_RejectsWithUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when --at is missing"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "create", StableSessionId, "--text", "ping"], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--at is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("USAGE", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionScheduleCreate_OffsetlessAt_RejectsWithUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for an offset-less --at"));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00", "--text", "ping"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("timezone offset", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("USAGE", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionScheduleCreate_NonTimestampAt_RejectsWithUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for a non-timestamp --at"));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "tomorrow", "--text", "ping"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("RFC 3339", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionScheduleCreate_MissingText_RejectsWithUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when --text is missing"));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--text is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("USAGE", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ----- create: request contract -----

    [Fact]
    public async Task SessionScheduleCreate_PostsOnlyTextAndDueAtWithGeneratedIdempotencyKey()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    scheduleId = "sch_1",
                    status = "scheduled",
                    dueAt = "2026-08-06T14:00:00+08:00",
                    text = "report progress",
                    inputId = (string?)null,
                    createdAt = "2026-08-06T10:00:00Z",
                    idempotencyKey = "generated-key",
                    cancelledAt = (string?)null,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "report progress"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/schedules",
            request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(2, body.Count);
        Assert.Equal("report progress", body["text"]?.GetValue<string>());
        // --at travels verbatim as the dueAt field; the CLI never converts
        // or normalizes the caller's offset.
        Assert.Equal("2026-08-06T14:00:00+08:00", body["dueAt"]?.GetValue<string>());
        var key = request.Headers["Idempotency-Key"].Single();
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.Contains($"Idempotency-Key: {key}", output.ToString(), StringComparison.Ordinal);

        var stdout = output.ToString();
        Assert.Contains("schedule id: sch_1", stdout, StringComparison.Ordinal);
        Assert.Contains("status:      scheduled", stdout, StringComparison.Ordinal);
        Assert.Contains("due at:      2026-08-06T14:00:00+08:00", stdout, StringComparison.Ordinal);
        Assert.Contains("text:        report progress", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCreate_UtcZAt_IsAcceptedAndSentVerbatim()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    scheduleId = "sch_z",
                    status = "scheduled",
                    dueAt = "2026-08-06T06:00:00Z",
                    text = "wake up",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T06:00:00Z", "--text", "wake up"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal("2026-08-06T06:00:00Z", body["dueAt"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionScheduleCreate_ExplicitKey_IsSentAndReusedOnRetry()
    {
        var attempts = 0;
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException("connection lost");
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { scheduleId = "sch_1", status = "scheduled", dueAt = "2026-08-06T14:00:00+08:00", text = "ping" },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "ping", "--idempotency-key", "retry-sch-1"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Equal("retry-sch-1", request.Headers["Idempotency-Key"].Single()));
        Assert.DoesNotContain("Idempotency-Key:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCreate_OmittedKey_PrintsGeneratedKeyBeforeRequest()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { scheduleId = "sch_1", status = "scheduled", dueAt = "2026-08-06T14:00:00+08:00", text = "ping" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "ping"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var key = handler.Requests.Single().Headers["Idempotency-Key"].Single();
        Assert.Contains($"Idempotency-Key: {key}", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCreate_JsonSelection_KeepsStdoutCleanAndPrintsKeyToStderr()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    scheduleId = "sch_1",
                    status = "scheduled",
                    dueAt = "2026-08-06T14:00:00+08:00",
                    text = "ping",
                    createdAt = "2026-08-06T10:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "ping", "--json", "scheduleId,status"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"scheduleId\": \"sch_1\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"scheduled\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Idempotency-Key:", output.ToString(), StringComparison.Ordinal);
        var key = handler.Requests.Single().Headers["Idempotency-Key"].Single();
        Assert.Contains($"Idempotency-Key: {key}", error.ToString(), StringComparison.Ordinal);
        Assert.Contains($"--idempotency-key {key}", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCreate_ServerRejectsPastDue_SurfacesError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "due time is in the past; use a normal follow-up instead",
                "schedule_due_in_past",
                HttpStatusCode.BadRequest)));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "ping"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("schedule_due_in_past", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("due time is in the past", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCreate_NoActiveProject_RejectsWithoutRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("API must not be called without a project"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "schedule", "create", StableSessionId, "--at", "2026-08-06T14:00:00+08:00", "--text", "ping"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(1, exitCode);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ----- list -----

    [Fact]
    public async Task SessionScheduleList_GetsSchedulesAndRendersTable()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        scheduleId = "sch_1",
                        status = "scheduled",
                        dueAt = "2026-08-06T14:00:00+08:00",
                        text = "report progress",
                        inputId = (string?)null,
                        createdAt = "2026-08-06T10:00:00Z",
                        idempotencyKey = "key-1",
                    },
                    new
                    {
                        scheduleId = "sch_2",
                        status = "delivered",
                        dueAt = "2026-08-06T08:00:00+08:00",
                        text = "check result",
                        inputId = (string?)"input-9",
                        createdAt = "2026-08-06T07:00:00Z",
                        idempotencyKey = "key-2",
                    },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "list", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/schedules",
            request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("schedule id", stdout, StringComparison.Ordinal);
        Assert.Contains("sch_1", stdout, StringComparison.Ordinal);
        Assert.Contains("scheduled", stdout, StringComparison.Ordinal);
        Assert.Contains("sch_2", stdout, StringComparison.Ordinal);
        Assert.Contains("delivered", stdout, StringComparison.Ordinal);
        Assert.Contains("report progress", stdout, StringComparison.Ordinal);
        Assert.Contains("input-9", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleList_Empty_RendersEmptyNotice()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "list", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No schedules", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleList_SelectedJson_ProjectsRequestedFields()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { scheduleId = "sch_1", status = "scheduled", dueAt = "2026-08-06T14:00:00+08:00", text = "ping" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "list", StableSessionId, "--json", "scheduleId,status"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"scheduleId\": \"sch_1\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"scheduled\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleList_ProjectOverride_UsesProjectArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "list", StableSessionId, "--project", "proj_other"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "/api/projects/proj_other/agent-sessions/sess_123/schedules",
            handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    // ----- cancel -----

    [Fact]
    public async Task SessionScheduleCancel_PostsCancelAndRendersCancelledStatus()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    scheduleId = "sch_1",
                    status = "cancelled",
                    dueAt = "2026-08-06T14:00:00+08:00",
                    text = "report progress",
                    cancelledAt = "2026-08-06T11:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "cancel", StableSessionId, "sch_1"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/schedules/sch_1/cancel",
            request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("schedule id: sch_1", stdout, StringComparison.Ordinal);
        Assert.Contains("status:      cancelled", stdout, StringComparison.Ordinal);
        Assert.Contains("cancelled:   2026-08-06T11:00:00Z", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCancel_Delivered_ReportsCurrentStatusHonestly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    scheduleId = "sch_2",
                    status = "delivered",
                    dueAt = "2026-08-06T08:00:00+08:00",
                    text = "check result",
                    inputId = "input-9",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "cancel", StableSessionId, "sch_2"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        var stdout = output.ToString();
        Assert.Contains("status:      delivered", stdout, StringComparison.Ordinal);
        Assert.Contains("input id:    input-9", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionScheduleCancel_UnknownSchedule_SurfacesNotFound()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Schedule sch_missing not found",
                "schedule_not_found",
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "cancel", StableSessionId, "sch_missing"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionScheduleCancel_SelectedJson_ProjectsStatus()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { scheduleId = "sch_1", status = "cancelled", dueAt = "2026-08-06T14:00:00+08:00", text = "ping" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "cancel", StableSessionId, "sch_1", "--json", "scheduleId,status"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"scheduleId\": \"sch_1\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"cancelled\"", output.ToString(), StringComparison.Ordinal);
    }

    // ----- output catalog -----

    [Fact]
    public void SessionSchedule_OutputCatalogMatchesLockedContract()
    {
        var expected = new[] { "scheduleId", "status", "dueAt", "text", "inputId", "createdAt", "idempotencyKey", "cancelledAt" };
        Assert.Equal(expected, ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionScheduleCreate)).Fields);
        Assert.Equal(expected, ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionScheduleList)).Fields);
        Assert.Equal(expected, ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionScheduleCancel)).Fields);
        Assert.Equal(
            ResourceCardinality.Collection,
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionScheduleList)).Cardinality);
    }
}
