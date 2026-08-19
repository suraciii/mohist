using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The public Job, Input, and Turn read routes of the direct API: every
/// answer is served only from the persisted public projection, anchored
/// to the requested canonical record, with canonical Project-membership
/// 404s after the grant passes and the checkpoint freshness gate that
/// turns an unconsumed source watermark into 503 projection_lag with a
/// retry hint — never a stale snapshot and never the five-state unknown.
/// </summary>
[Collection("PublicProjectionIntegration")]
public sealed class PublicExecutionReadRouteSpecs(PublicProjectionIntegrationFixture fixture)
{
    private const string Prompt = "Investigate the failed deployment";

    private static readonly string[] AllowlistedKeys =
    [
        "projectId", "agentId", "jobId", "sessionId", "inputId", "turnId",
        "status", "jobStatus", "sessionActivity", "admission", "inputStatus",
        "turnStatus", "outcome", "reasonCode", "output", "error",
        "acceptedAt", "queuedAt", "startedAt", "terminalAt", "observedAt",
        "sequence",
    ];

    private static readonly string[] FiveStates =
    [
        PublicExecutionFieldValues.StatusAccepted,
        PublicExecutionFieldValues.StatusQueued,
        PublicExecutionFieldValues.StatusRunning,
        PublicExecutionFieldValues.StatusTerminal,
        PublicExecutionFieldValues.StatusUnknown,
    ];

    [Fact]
    public async Task PreparedJob_IsReadAsAcceptedPreparingOnItsOwnAnchor()
    {
        var projectId = await SeedProjectAsync();
        var jobId = $"job-prepared-{Guid.NewGuid():N}";
        await SeedJobAsync(jobId, projectId, "agent_pub", sessionId: null, inputId: null, turnId: null);

        using var body = await ReadJobAsync(projectId, jobId);

        Assert.Equal(PublicExecutionFieldValues.StatusAccepted, body.RootElement.GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.JobPreparing, body.RootElement.GetProperty("jobStatus").GetString());
        Assert.Equal(jobId, body.RootElement.GetProperty("jobId").GetString());
        Assert.Null(body.RootElement.GetProperty("sessionId").GetString());
        Assert.Null(body.RootElement.GetProperty("inputId").GetString());
        Assert.Null(body.RootElement.GetProperty("turnId").GetString());
        await AssertAllowlistAsync(body);
    }

    [Fact]
    public async Task AcceptedJobWithTerminalTurn_ExposesLiveReferencesOnAllThreeReads()
    {
        var projectId = await SeedProjectAsync();
        var ids = NewLaunchIds("joined");
        await SeedLaunchAsync(
            projectId,
            ids,
            jobStatus: AgentJobStatus.Completed,
            terminalResult: CompletedResult(),
            activity: AgentSessionActivity.Idle,
            turnStatus: AgentTurnStatus.Completed,
            turnResult: new AgentTurnResult(Output: """{"text":"The deployment failure was a bad config."}"""));

        using var reader = await CreateReaderAsync(projectId);
        using var job = await ReadAsync(
            reader,
            $"/api/v1/projects/{projectId}/agent-jobs/{ids.JobId}",
            root => string.Equals(
                root.GetProperty("status").GetString(),
                PublicExecutionFieldValues.StatusTerminal,
                StringComparison.Ordinal));
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, Json(job).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.OutcomeCompleted, Json(job).GetProperty("outcome").GetString());
        Assert.Equal(ids.SessionId, Json(job).GetProperty("sessionId").GetString());
        Assert.Equal(ids.InputId, Json(job).GetProperty("inputId").GetString());
        Assert.Equal(ids.TurnId, Json(job).GetProperty("turnId").GetString());
        Assert.Equal(
            "The deployment failure was a bad config.",
            Json(job).GetProperty("output").GetProperty("text").GetString());
        Assert.True(Json(job).GetProperty("sequence").GetInt64() > 0);
        await AssertAllowlistAsync(job);

        using var input = await ReadInputAsync(projectId, ids.InputId);
        Assert.Equal(ids.InputId, Json(input).GetProperty("inputId").GetString());
        Assert.Equal(PublicExecutionFieldValues.InputAccepted, Json(input).GetProperty("inputStatus").GetString());
        await AssertAllowlistAsync(input);

        using var turn = await ReadTurnAsync(projectId, ids.TurnId);
        Assert.Equal(ids.TurnId, Json(turn).GetProperty("turnId").GetString());
        Assert.Equal(PublicExecutionFieldValues.OutcomeCompleted, Json(turn).GetProperty("outcome").GetString());
        await AssertAllowlistAsync(turn);
    }

    [Fact]
    public async Task TerminalTurnInsideActiveSession_StaysTerminalAnchoredToItsOwnRecord()
    {
        var projectId = await SeedProjectAsync();
        var firstTurn = $"turn-first-{Guid.NewGuid():N}";
        var secondTurn = $"turn-second-{Guid.NewGuid():N}";
        var firstInput = $"input-first-{Guid.NewGuid():N}";
        var secondInput = $"input-second-{Guid.NewGuid():N}";
        var sessionId = $"session-active-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;

        await SeedSessionAsync(sessionId, projectId, "agent_pub", activity: AgentSessionActivity.Active, inputs:
        [
            new AgentSessionInputRecord(
                Id: firstInput,
                Sequence: 1,
                Text: Prompt,
                Source: "direct-test",
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now),
            new AgentSessionInputRecord(
                Id: secondInput,
                Sequence: 2,
                Text: Prompt,
                Source: "direct-test",
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now.AddMinutes(3)),
        ], turns:
        [
            new AgentTurnRecord(
                Id: firstTurn,
                Sequence: 1,
                InputIds: [firstInput],
                Status: AgentTurnStatus.Completed,
                RecordedAt: now,
                UpdatedAt: now.AddMinutes(2)),
            new AgentTurnRecord(
                Id: secondTurn,
                Sequence: 2,
                InputIds: [secondInput],
                Status: AgentTurnStatus.Executing,
                RecordedAt: now.AddMinutes(3),
                UpdatedAt: now.AddMinutes(4)),
        ]);

        // The terminal Turn keeps its own terminal observation while the
        // enclosing Session runs a later Turn: sessionActivity is context,
        // never a state override.
        using var terminal = await ReadTurnAsync(projectId, firstTurn);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, Json(terminal).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.OutcomeCompleted, Json(terminal).GetProperty("outcome").GetString());
        Assert.Equal(PublicExecutionFieldValues.SessionActive, Json(terminal).GetProperty("sessionActivity").GetString());
        await AssertAllowlistAsync(terminal);

        using var running = await ReadTurnAsync(projectId, secondTurn);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, Json(running).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.TurnRunning, Json(running).GetProperty("turnStatus").GetString());

        // The first Input stays anchored to its own terminal Turn, not to
        // the Session's latest running Turn: an active Session never turns
        // a terminal anchor into running.
        using var input = await ReadInputAsync(projectId, firstInput);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, Json(input).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.InputAccepted, Json(input).GetProperty("inputStatus").GetString());
        Assert.Equal(PublicExecutionFieldValues.SessionActive, Json(input).GetProperty("sessionActivity").GetString());
    }

    [Fact]
    public async Task DurableSessionRejection_JobReadIsTerminalRejectedWithSafeError()
    {
        var projectId = await SeedProjectAsync();
        var ids = NewLaunchIds("rejected");
        await SeedLaunchAsync(
            projectId,
            ids,
            jobStatus: AgentJobStatus.Pending,
            terminalResult: null,
            activity: AgentSessionActivity.Idle,
            turnStatus: null,
            turnResult: null,
            inputAcceptance: AgentSessionInputAcceptance.Rejected);

        using var body = await ReadJobAsync(projectId, ids.JobId);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, Json(body).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.OutcomeRejected, Json(body).GetProperty("outcome").GetString());
        Assert.Null(Json(body).GetProperty("turnId").GetString());

        // The error carries only a stable public code and a safe message.
        var error = Json(body).GetProperty("error");
        Assert.Equal(PublicExecutionFieldValues.OutcomeRejected, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        await AssertAllowlistAsync(body);
    }

    [Fact]
    public async Task MissingAndForeignResources_AnswerTheCanonical404Codes_AfterTheGrantPasses()
    {
        var projectId = await SeedProjectAsync();
        var otherProject = await SeedProjectAsync();
        using var client = await CreateReaderAsync(projectId);

        // Foreign live resources: projected, but belonging to another
        // Project — indistinguishable from missing ones.
        var foreign = NewLaunchIds("foreign");
        await SeedLaunchAsync(otherProject, foreign, AgentJobStatus.Pending, null, AgentSessionActivity.Active, AgentTurnStatus.Queued, null);

        foreach (var (path, code) in new[]
        {
            ($"/api/v1/projects/{projectId}/agent-jobs/job-missing-{Guid.NewGuid():N}", "job_not_found"),
            ($"/api/v1/projects/{projectId}/agent-jobs/{foreign.JobId}", "job_not_found"),
            ($"/api/v1/projects/{projectId}/agent-inputs/input-missing-{Guid.NewGuid():N}", "input_not_found"),
            ($"/api/v1/projects/{projectId}/agent-inputs/{foreign.InputId}", "input_not_found"),
            ($"/api/v1/projects/{projectId}/agent-turns/turn-missing-{Guid.NewGuid():N}", "turn_not_found"),
            ($"/api/v1/projects/{projectId}/agent-turns/{foreign.TurnId}", "turn_not_found"),
        })
        {
            using var response = await client.GetAsync(path);
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{path} answered {response.StatusCode}, expected 404 {code}.");
            await AssertErrorAsync(response, HttpStatusCode.NotFound, code);
        }
    }

    [Fact]
    public async Task ProjectionLag_Answers503WithRetryAfter_NoStaleBody_AndRecoversWhenTheProjectorCatchesUp()
    {
        var projectId = await SeedProjectAsync();
        var jobId = $"job-lag-{Guid.NewGuid():N}";
        await SeedJobAsync(jobId, projectId, "agent_pub", sessionId: null, inputId: null, turnId: null);
        using var client = await CreateReaderAsync(projectId);
        using var current = await ReadAsync(client, $"/api/v1/projects/{projectId}/agent-jobs/{jobId}");
        Assert.Equal(PublicExecutionFieldValues.StatusAccepted, Json(current).GetProperty("status").GetString());

        // Rewind the stored checkpoint below the durable source head: the
        // same request must now refuse to serve the (still existing)
        // snapshot as current state. The hosted projector re-advances a
        // rewound checkpoint as soon as its drain observes the mismatch,
        // so the checkpoint is re-pinned on each attempt until the read
        // actually observes the gate — a bounded probe that fails loudly
        // if the 503 never appears.
        HttpResponseMessage? lagged = null;
        try
        {
            await TestWait.ForAsync(
                probe: async () =>
                {
                    await RewindJobCheckpointAsync(jobId);
                    lagged?.Dispose();
                    var response = await client.GetAsync($"/api/v1/projects/{projectId}/agent-jobs/{jobId}");
                    lagged = response;
                    return response.StatusCode == HttpStatusCode.ServiceUnavailable;
                },
                isDone: ok => ok,
                timeout: TimeSpan.FromSeconds(10),
                step: TimeSpan.FromMilliseconds(30),
                description: "the rewound job checkpoint to gate job reads with projection_lag");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, lagged!.StatusCode);
            Assert.Equal(
                DirectApiResults.ProjectionLagRetryAfterSeconds,
                lagged.Headers.RetryAfter?.ToString());
            await AssertErrorAsync(lagged, HttpStatusCode.ServiceUnavailable, "projection_lag");
            var laggedBody = await lagged.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"status\"", laggedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("\"jobId\"", laggedBody, StringComparison.Ordinal);
        }
        finally
        {
            lagged?.Dispose();
        }

        // The projector repairs the checkpoint from the durable source;
        // the same read then serves the current projection again.
        await NudgeProjectorAsync();
        using var recovered = await ReadAsync(client, $"/api/v1/projects/{projectId}/agent-jobs/{jobId}");
        Assert.Equal(PublicExecutionFieldValues.StatusAccepted, Json(recovered).GetProperty("status").GetString());
        Assert.Equal(PublicExecutionFieldValues.JobPreparing, Json(recovered).GetProperty("jobStatus").GetString());
    }

    [Fact]
    public async Task SessionCheckpointLag_GatesInputAndTurnReadsUntilCaughtUp()
    {
        var projectId = await SeedProjectAsync();
        var ids = NewLaunchIds("sessionlag");
        await SeedLaunchAsync(
            projectId,
            ids,
            AgentJobStatus.Running,
            null,
            AgentSessionActivity.Active,
            AgentTurnStatus.Executing,
            null);
        using var client = await CreateReaderAsync(projectId);
        var inputPath = $"/api/v1/projects/{projectId}/agent-inputs/{ids.InputId}";
        using var current = await ReadAsync(client, inputPath);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, Json(current).GetProperty("status").GetString());

        // The Session ledger digest is the session anchor's required
        // source watermark: a checkpoint holding an older digest gates
        // Input and Turn reads with projection_lag, and never surfaces
        // the stale snapshot as the five-state unknown. The hosted
        // projector re-advances a rewound checkpoint as soon as its
        // drain observes the mismatch, so the checkpoint is re-pinned on
        // each attempt until both reads actually observe the gate — a
        // bounded probe that fails loudly if the gate never fires.
        HttpResponseMessage? inputLagged = null;
        HttpResponseMessage? turnLagged = null;
        try
        {
            await TestWait.ForAsync(
                probe: async () =>
                {
                    await RewindSessionCheckpointAsync(ids.SessionId);
                    inputLagged?.Dispose();
                    turnLagged?.Dispose();
                    var laggedReads = await Task.WhenAll(
                        client.GetAsync(inputPath),
                        client.GetAsync($"/api/v1/projects/{projectId}/agent-turns/{ids.TurnId}"));
                    inputLagged = laggedReads[0];
                    turnLagged = laggedReads[1];
                    return inputLagged.StatusCode == HttpStatusCode.ServiceUnavailable
                        && turnLagged.StatusCode == HttpStatusCode.ServiceUnavailable;
                },
                isDone: ok => ok,
                timeout: TimeSpan.FromSeconds(10),
                step: TimeSpan.FromMilliseconds(30),
                description: "the rewound session checkpoint to gate input and turn reads with projection_lag");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, inputLagged!.StatusCode);
            await AssertErrorAsync(inputLagged, HttpStatusCode.ServiceUnavailable, "projection_lag");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, turnLagged!.StatusCode);
            await AssertErrorAsync(turnLagged, HttpStatusCode.ServiceUnavailable, "projection_lag");
        }
        finally
        {
            inputLagged?.Dispose();
            turnLagged?.Dispose();
        }

        await NudgeProjectorAsync();
        using var inputRecovered = await ReadAsync(client, inputPath);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, Json(inputRecovered).GetProperty("status").GetString());
    }

    // --- helpers ---

    private sealed record LaunchIds(string JobId, string SessionId, string InputId, string TurnId);

    private static LaunchIds NewLaunchIds(string tag) => new(
        $"job-{tag}-{Guid.NewGuid():N}",
        $"session-{tag}-{Guid.NewGuid():N}",
        $"input-{tag}-{Guid.NewGuid():N}",
        $"turn-{tag}-{Guid.NewGuid():N}");

    private async Task<string> SeedProjectAsync()
    {
        var projectId = $"direct-read-{Guid.NewGuid():N}";
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<HttpClient> CreateReaderAsync(string projectId)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name = $"direct-read-{Guid.NewGuid():N}",
            scope = "readonly",
            projectIds = new[] { projectId },
        });
        response.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")
            .GetProperty("token")
            .GetString()!;

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task SeedLaunchAsync(
        string projectId,
        LaunchIds ids,
        AgentJobStatus jobStatus,
        AgentJobTerminalResult? terminalResult,
        AgentSessionActivity activity,
        AgentTurnStatus? turnStatus,
        AgentTurnResult? turnResult,
        AgentSessionInputAcceptance inputAcceptance = AgentSessionInputAcceptance.Accepted)
    {
        await SeedJobAsync(
            ids.JobId,
            projectId,
            "agent_pub",
            ids.SessionId,
            ids.InputId,
            ids.TurnId,
            jobStatus,
            terminalResult);

        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var inputs = new List<AgentSessionInputRecord>
        {
            new(
                Id: ids.InputId,
                Sequence: 1,
                Text: Prompt,
                Source: "direct-test",
                Acceptance: inputAcceptance,
                RecordedAt: now,
                JobId: ids.JobId),
        };
        var turns = new List<AgentTurnRecord>();
        if (turnStatus is { } status)
        {
            turns.Add(new AgentTurnRecord(
                Id: ids.TurnId!,
                Sequence: 1,
                InputIds: [ids.InputId],
                Status: status,
                JobId: ids.JobId,
                Result: turnResult,
                RecordedAt: now,
                UpdatedAt: now.AddMinutes(2)));
        }

        await SeedSessionAsync(ids.SessionId, projectId, "agent_pub", activity, inputs, turns);
    }

    private async Task SeedJobAsync(
        string jobKey,
        string projectId,
        string agentId,
        string? sessionId,
        string? inputId,
        string? turnId,
        AgentJobStatus status = AgentJobStatus.Pending,
        AgentJobTerminalResult? terminalResult = null)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var now = fixture.TimeProvider.GetUtcNow();
        var state = new AgentJobState
        {
            Status = status,
            TerminalResult = terminalResult,
            SubmittedAt = now,
            Input = new AgentJobInput(
                Prompt: Prompt,
                ProjectId: projectId,
                AgentId: agentId,
                AgentSessionId: sessionId,
                InitialInputId: inputId,
                InitialTurnId: turnId),
        };
        await jobs.InsertLedgerAsync(new AgentJobLedgerRecord(
            JobKey: jobKey,
            StateJson: JsonSerializer.Serialize(state, JSON.Options),
            Revision: 0,
            AssignedRunnerId: null,
            WorkId: null,
            ReadySince: status == AgentJobStatus.Pending ? now : null,
            RunningSince: null,
            DispatchJson: null,
            WorkType: null,
            Stage: null,
            Title: null,
            IssueProjectId: null,
            IssueNumber: null,
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId));
    }

    private async Task SeedSessionAsync(
        string sessionId,
        string projectId,
        string agentId,
        AgentSessionActivity activity,
        IReadOnlyList<AgentSessionInputRecord> inputs,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var session = AgentSession.Create(
            sessionId,
            "runner-1",
            "/mohist-tests/work",
            new AgentSessionMetadata(Labels: new Dictionary<string, string>
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = agentId,
            }),
            fixture.TimeProvider.GetUtcNow().UtcDateTime);
        session.Status = session.Status with
        {
            Activity = activity,
            Inputs = inputs,
            Turns = turns,
        };
        await sessions.SaveAsync(session.Id, session);
    }

    private static AgentJobTerminalResult CompletedResult() => new(
        AgentJobStatus.Completed,
        Message: "Done",
        Output: """{"text":"The deployment failure was a bad config."}""",
        ArtifactUploadIds: null,
        FailureReason: null,
        ExitCode: 0);

    private async Task<JsonDocument> ReadJobAsync(string projectId, string jobId)
    {
        using var client = await CreateReaderAsync(projectId);
        return await ReadAsync(client, $"/api/v1/projects/{projectId}/agent-jobs/{jobId}");
    }

    private async Task<JsonDocument> ReadInputAsync(string projectId, string inputId)
    {
        using var client = await CreateReaderAsync(projectId);
        return await ReadAsync(client, $"/api/v1/projects/{projectId}/agent-inputs/{inputId}");
    }

    private async Task<JsonDocument> ReadTurnAsync(string projectId, string turnId)
    {
        using var client = await CreateReaderAsync(projectId);
        return await ReadAsync(client, $"/api/v1/projects/{projectId}/agent-turns/{turnId}");
    }

    private async Task<JsonDocument> ReadAsync(
        HttpClient client,
        string path,
        Func<JsonElement, bool>? isReady = null)
    {
        await NudgeProjectorAsync();
        using var response = await client.GetAsync(path);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {response.StatusCode}: {content}");
        var document = JsonDocument.Parse(content);
        Assert.True(isReady is null || isReady(document.RootElement), content);
        return document;
    }
    private async Task RewindJobCheckpointAsync(string jobKey)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var checkpoint = await db.PublicProjectionCheckpoints.SingleAsync(
            row => row.Feed == PublicProjectionFeeds.AgentJobs && row.SourceKey == jobKey);
        var revision = await db.AgentJobs.AsNoTracking()
            .Where(row => row.JobKey == jobKey)
            .Select(row => row.Revision)
            .SingleAsync();
        checkpoint.Watermark = (revision - 1).ToString();
        await db.SaveChangesAsync();
    }

    private async Task RewindSessionCheckpointAsync(string sessionId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var checkpoint = await db.PublicProjectionCheckpoints.SingleAsync(
            row => row.Feed == PublicProjectionFeeds.AgentSessions && row.SourceKey == sessionId);
        checkpoint.Watermark = "rewound-below-the-consumed-digest";
        await db.SaveChangesAsync();
    }

    private async Task NudgeProjectorAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PublicProjectionNudge>().NudgeAndWaitAsync();
    }

    private static JsonElement Json(JsonDocument document) => document.RootElement;

    /// <summary>
    /// The response-body contract of every projection-sourced read: the
    /// exact allowlisted key set, the public field vocabulary, RFC 3339
    /// UTC timestamps with observedAt always present, an output that is
    /// null or a text object, and none of the excluded internal detail.
    /// </summary>
    private static async Task AssertAllowlistAsync(JsonDocument body)
    {
        var root = body.RootElement;
        var keys = root.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.True(
            keys.SetEquals(AllowlistedKeys),
            $"Expected exactly the allowlisted keys; extra=[{string.Join(", ", keys.Except(AllowlistedKeys))}] missing=[{string.Join(", ", AllowlistedKeys.Except(keys))}]");

        Assert.Contains(root.GetProperty("status").GetString(), FiveStates);
        if (root.GetProperty("jobStatus").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("jobStatus").GetString(),
                new[] { "preparing", "queued", "running", "terminal", "unknown" });
        }

        if (root.GetProperty("sessionActivity").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("sessionActivity").GetString(),
                new[] { "idle", "active", "unknown" });
        }

        if (root.GetProperty("admission").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("admission").GetString(),
                new[] { "ready", "blocked" });
        }

        if (root.GetProperty("inputStatus").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("inputStatus").GetString(),
                new[] { PublicExecutionFieldValues.InputAccepted, PublicExecutionFieldValues.InputRejected, PublicExecutionFieldValues.InputUnknown });
        }

        if (root.GetProperty("turnStatus").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("turnStatus").GetString(),
                new[] { "queued", "running", "outcome_pending", "terminal", "unknown" });
        }

        if (root.GetProperty("outcome").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(
                root.GetProperty("outcome").GetString(),
                new[] { "completed", "rejected", "failed", "cancelled", "blocked" });
        }

        // output is null or { "text": ... } with persisted final text only.
        if (root.GetProperty("output").ValueKind != JsonValueKind.Null)
        {
            var output = root.GetProperty("output");
            Assert.Equal(["text"], output.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.String, output.GetProperty("text").ValueKind);
        }

        // error carries only a stable public code and a safe message.
        if (root.GetProperty("error").ValueKind != JsonValueKind.Null)
        {
            var error = root.GetProperty("error");
            Assert.Equal(["code", "message"], error.EnumerateObject().Select(property => property.Name));
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }

        // reasonCode is null or one stable safe public code.
        if (root.GetProperty("reasonCode").ValueKind != JsonValueKind.Null)
        {
            var reason = root.GetProperty("reasonCode").GetString();
            Assert.Contains(
                reason,
                new[]
                {
                    PublicExecutionFieldValues.Reasons.QueueFull,
                    PublicExecutionFieldValues.Reasons.ContextReset,
                    PublicExecutionFieldValues.Reasons.StopOutcomeUnknown,
                });
        }

        // Timestamps are RFC 3339 UTC instants and observedAt is present.
        foreach (var key in new[] { "acceptedAt", "queuedAt", "startedAt", "terminalAt" })
        {
            if (root.GetProperty(key).ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            Assert.EndsWith("Z", root.GetProperty(key).GetString());
        }

        Assert.Equal(JsonValueKind.String, root.GetProperty("observedAt").ValueKind);
        Assert.EndsWith("Z", root.GetProperty("observedAt").GetString());

        // No unlisted execution property ever appears — none of the
        // excluded internal detail, and no prompt or input text.
        var raw = root.GetRawText();
        foreach (var banned in new[]
                 {
                     "runtimeSessionId", "runnerId", "runtime", "bindingEpoch", "connectionId",
                     "lease", "fence", "operationId", "attemptId", "dispatch", "retry",
                     "prompt", "instructions", "memory", "tool", "workspace",
                     "workdir", "path", "attachment", "payload", "transcript", "provider",
                     "stack", Prompt,
                 })
        {
            Assert.DoesNotContain(banned, raw, StringComparison.Ordinal);
        }
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(["error"], root.EnumerateObject().Select(property => property.Name));
        var error = root.GetProperty("error");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }
}
