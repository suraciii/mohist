using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

/// <summary>
/// Issue-318 T-002 specs for the runner-grain poll path. Per design D4:
/// <list type="bullet">
/// <item><c>PollAssignedOrAssignableWorkflowAsync</c> calls <c>PollWorkAsync</c>
/// directly on each <c>FindAssignedToAsync</c> row. The previous
/// <c>GetCurrentWorkIdAsync</c> busy pre-check (~104 grain calls/s) is
/// gone, because the new state machine's <c>Ready</c> status already
/// excludes in-flight work.</item>
/// <item><c>ActiveWorkflowCountAsync</c> counts <c>status == Running</c>
/// rows for the runner via a new <c>CountRunningAssignedToAsync</c> query.
/// The previous implementation reused <c>FindAssignedToAsync</c> +
/// <c>GetCurrentWorkIdAsync</c> and would have collapsed to 0 once
/// <c>Ready</c> excluded in-flight work — so the slot-budget gate in
/// <c>PollAsync</c> would have let the runner exceed its
/// <c>MaxWorkflowSlots</c>.</item>
/// </list>
/// </summary>
[Collection("WorkflowGrain")]
public class RunnerPollSchedulingSpecs : Mohist.Server.Tests.Specs.Workflow.WorkflowGrainSpecs
{
    public RunnerPollSchedulingSpecs(Mohist.Server.Tests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollAsync_ReadyWorkflowIsDispatchedDirectly()
    {
        // Set up a Pending workflow, assign it to a fresh runner, then
        // poll. The new code path surfaces the workflow through
        // FindAssignedToAsync (status=Ready AND runner=<this>) and
        // calls PollWorkAsync directly — there is no GetCurrentWorkIdAsync
        // pre-check that could short-circuit pickup. The runner should
        // get back a WorkDispatch for the only ready task.
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();

        Assert.NotNull(work);
        Assert.Equal(_workflowId, work!.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollAsync_RespectsSlotBudget_WhenRunningWorkflowIsAlreadyAssigned()
    {
        // ActiveWorkflowCountAsync counts Running rows for the runner.
        // Under the old code the count collapsed to 0 once
        // FindAssignedToAsync only returned Ready (idle) rows, so the
        // slot gate would let the runner pick up additional work even
        // when it already had MaxWorkflowSlots=1 in flight. The fix in
        // T-002 (CountRunningAssignedToAsync) restores the correct
        // count so the gate stops the second pickup.
        var projectId = "runner-slot-budget";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // Seed two workflows assigned to the same runner. The first
        // gets picked up and runs to Running; the second is still
        // Pending (unclaimed). After the first poll the runner should
        // refuse the second because its slot budget is exhausted.
        var workflowAId = $"wf-running-{Guid.NewGuid():N}";
        var workflowBId = $"wf-pending-{Guid.NewGuid():N}";
        var workflowA = Grains.GetGrain<IWorkflowGrain>(workflowAId);
        var workflowB = Grains.GetGrain<IWorkflowGrain>(workflowBId);
        await SeedWorkflowTemplateAsync(workflowAId, SingleStage(checks: []), projectId);
        await SeedWorkflowTemplateAsync(workflowBId, SingleStage(checks: []), projectId);
        await workflowA.StartAsync(TestInput(projectId));
        await workflowA.AssignRunnerAsync(runnerId);
        await workflowB.StartAsync(TestInput(projectId));

        var firstDispatch = await runner.PollAsync();
        Assert.NotNull(firstDispatch);
        Assert.Equal(workflowAId, firstDispatch!.WorkflowRunId);

        // The next poll must NOT pick up workflowB — the slot budget is
        // 1 and the count query sees the in-flight Running workflow.
        var secondDispatch = await runner.PollAsync();
        Assert.Null(secondDispatch);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task CountRunningAssignedToAsync_ReturnsRunningRowsForTheRunner()
    {
        // Direct spec for the count query — three Running rows for
        // runner A, one for runner B, one Ready for runner A, one
        // terminal for runner A. The querier is the same one
        // ActiveWorkflowCountAsync now uses, so its correctness is
        // what keeps the slot gate honest.
        var prefix = $"count-{Guid.NewGuid():N}";
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertStatusRowAsync($"{prefix}-run-1", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-2", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-3", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-ready-A", "Ready", runnerA);
        await InsertStatusRowAsync($"{prefix}-completed-A", "Completed", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-B", "Running", runnerB);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(3, await querier.CountRunningAssignedToAsync(runnerA));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync(runnerB));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollAssignedOrAssignableWorkflowAsync_DoesNotCallGetCurrentWorkIdAsync()
    {
        // Structural check on the poll loop body: the source of
        // RunnerGrain.PollAssignedOrAssignableWorkflowAsync must not
        // call GetCurrentWorkIdAsync anymore. The pre-check (~104
        // grain calls/s) is gone under the new state machine because
        // Ready already excludes in-flight work. The body is read from
        // disk so a regression in the source file is caught even if the
        // existing behavioural tests happen to pass (the behavioural
        // test for Ready cannot observe the dropped pre-check since
        // both code paths would have dispatched the work).
        var runnerGrainPath = Path.Combine(
            GetProjectRoot(),
            "src", "Mohist.Server", "Runner", "Grains", "RunnerGrain.cs");
        var source = await File.ReadAllTextAsync(runnerGrainPath);

        var methodBody = ExtractMethodBody(source, "PollAssignedOrAssignableWorkflowAsync");
        var executable = StripCSharpComments(methodBody);

        Assert.DoesNotContain("GetCurrentWorkIdAsync", executable, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ActiveWorkflowCountAsync_UsesCountRunningAssignedToAsync()
    {
        // Structural check: ActiveWorkflowCountAsync's body must read
        // status == Running via CountRunningAssignedToAsync, not via
        // the previous FindAssignedToAsync + GetCurrentWorkIdAsync
        // fan-out. Without this, the slot-budget gate in PollAsync
        // collapses to 0 under the new state machine and the runner
        // over-dispatches beyond MaxWorkflowSlots.
        var runnerGrainPath = Path.Combine(
            GetProjectRoot(),
            "src", "Mohist.Server", "Runner", "Grains", "RunnerGrain.cs");
        var source = await File.ReadAllTextAsync(runnerGrainPath);

        var methodBody = ExtractMethodBody(source, "ActiveWorkflowCountAsync");
        var executable = StripCSharpComments(methodBody);

        Assert.Contains("CountRunningAssignedToAsync", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentWorkIdAsync", executable, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes <c>// line</c> and <c>/* block *\/</c> comments and string
    /// literals from a snippet of C# source so the structural "no
    /// GetCurrentWorkIdAsync call" assertion is not fooled by a
    /// documentation comment that name-checks the symbol. Strings are
    /// blanked to avoid the same false positive inside string
    /// literals. Adequate for the small bodies in RunnerGrain.cs;
    /// deliberately not a full C# parser.
    /// </summary>
    private static string StripCSharpComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 2;
                output.Append("  ");
                continue;
            }

            // Line comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i + 2);
                if (end < 0) end = source.Length;
                i = end;
                continue;
            }

            // String literal
            if (source[i] == '"')
            {
                output.Append('"');
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) i++;
                    output.Append(' ');
                    i++;
                }
                if (i < source.Length)
                {
                    output.Append('"');
                    i++;
                }
                continue;
            }

            output.Append(source[i]);
            i++;
        }

        return output.ToString();
    }

    private static string GetProjectRoot()
    {
        // Test assembly lives at packages/server/tests/Mohist.Server.Tests/bin/<config>/<tfm>/.
        // Source file is at packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
        // (i.e. five levels up from the assembly directory lands on
        // packages/server/, the directory containing both src/ and tests/).
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot locate test assembly directory");
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    /// <summary>
    /// Naïve-but-sufficient extractor for a private method body. Locates
    /// the method name as a word token (any leading whitespace) and reads
    /// until the matching closing brace at depth 0. Adequate for the
    /// small bodies in RunnerGrain.cs; deliberately not a full C#
    /// parser.
    /// </summary>
    private static string ExtractMethodBody(string source, string methodName)
    {
        var idx = -1;
        var token = $"{methodName}(";
        for (var scan = 0; scan < source.Length - token.Length; scan++)
        {
            if (source[scan] == ' ' || source[scan] == '\t' || source[scan] == '\n' || source[scan] == '\r')
            {
                if (string.CompareOrdinal(source, scan + 1, token, 0, token.Length) == 0)
                {
                    idx = scan + 1;
                    break;
                }
            }
        }
        Assert.True(idx > 0, $"Could not locate method '{methodName}' in RunnerGrain.cs");

        var braceIdx = source.IndexOf('{', idx);
        Assert.True(braceIdx > 0, $"Could not locate opening brace for method '{methodName}'");

        var depth = 0;
        for (var i = braceIdx; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return source.Substring(braceIdx, i - braceIdx + 1);
                    break;
            }
        }

        throw new InvalidOperationException($"Unterminated method body for '{methodName}'");
    }

    /// <summary>
    /// Inserts a <c>WorkflowRuns</c> row with the requested status and
    /// runner. Mirrors the schema the runner-grain reads at runtime:
    /// State.status is camelCase via the JSON serializer and the
    /// STORED Status computed column gets its value from a JSON extract
    /// in the production migration (here populated via the trigger
    /// installed by <c>GrainTestConfig.ApplyWorkflowRunsStatusSchemaFix</c>).
    /// </summary>
    private async Task InsertStatusRowAsync(
        string workflowRunId,
        string status,
        string runnerId)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
                "spec/workflow",
                [new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])]));
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks =
            {
                new TaskRun
                {
                    Id = "task-1",
                    DefinitionId = "task-1",
                    Attempt = 1,
                    Title = "Task 1",
                    Status = status == "Running"
                        ? TaskRunStatus.Running
                        : TaskRunStatus.Pending,
                },
            },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = new WorkflowAssignment(runnerId, DateTimeOffset.UtcNow);

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }
}