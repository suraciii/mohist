using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentJobWriteThroughMirrorSpecs : AgentJobGrainTestSupport
{
    public AgentJobWriteThroughMirrorSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Mirror_SubmitWritesRowWithProjectAndAgent()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"mirror-submit-runner-{Guid.NewGuid():N}");
        var jobKey = $"mirror-submit-{Guid.NewGuid():N}";

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "mirror test",
            WorkspacePath: "/tmp/mirror-submit",
            ProjectId: projectId,
            AgentId: "agent-mirror"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == jobKey);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal("agent-mirror", row.AgentId);
    }

    [Fact]
    public async Task Mirror_RunnerAcceptanceWritesRunningStatus()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"mirror-accept-runner-{Guid.NewGuid():N}");
        var jobKey = $"mirror-accept-{Guid.NewGuid():N}";

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "accept mirror",
            WorkspacePath: "/tmp/mirror-accept",
            ProjectId: projectId,
            AgentId: "agent-accept"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == jobKey);
        Assert.Equal("running", row.Status);
    }

    [Fact]
    public async Task Mirror_TerminalTransitionWritesRowWithTerminalStatusAndAt()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"mirror-terminal-runner-{Guid.NewGuid():N}");
        var jobKey = $"mirror-terminal-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "terminal mirror",
            WorkspacePath: "/tmp/mirror-terminal",
            ProjectId: projectId,
            AgentId: "agent-terminal"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportAgentJobResultAsync(jobKey, workId,
            new WorkResult("completed", "ok", Output: JSON.DeserializeElement("{}"), ExitCode: 0));

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == jobKey);
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.TerminalAt);
    }

    [Fact]
    public async Task Mirror_FailedTerminalTransitionWritesFailureReason()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"mirror-fail-runner-{Guid.NewGuid():N}");
        var jobKey = $"mirror-fail-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "fail mirror",
            WorkspacePath: "/tmp/mirror-fail",
            ProjectId: projectId,
            AgentId: "agent-fail"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportAgentJobResultAsync(jobKey, workId,
            new WorkResult("failed", "boom", ExitCode: 1));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == jobKey);
        Assert.Equal("failed", row.Status);
        Assert.NotNull(row.TerminalAt);
    }

    [Fact]
    public async Task Mirror_PersistentStateRemainsAuthoritative()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"mirror-authority-runner-{Guid.NewGuid():N}");
        var jobKey = $"mirror-authority-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "authority test",
            WorkspacePath: "/tmp/mirror-authority",
            ProjectId: projectId,
            AgentId: "agent-authority"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == jobKey);
        Assert.Equal(projectId, row.ProjectId);

        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }
}
