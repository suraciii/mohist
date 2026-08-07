using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

using static IssueMetricsTestSupport;

[Collection("MohistDb")]
public sealed class IssueMetricsQualityStageOrderSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsQualityStageOrderSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetQualityAsync_CustomDefinition_UsesDeclaredOrderWithoutBuiltinGhosts()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-custom-{Guid.NewGuid():N}", Name = "Quality Custom" };
        const string profileId = "custom/quality";
        const string workflowRunId = "wr_quality_custom_1";
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
        {
            ProjectId = project.Id,
            ProfileId = profileId,
            Name = "Custom quality",
            DefinitionSource = """
                id: custom/quality
                stages:
                  - stage: zebra
                    tasks: []
                    checks: []
                  - stage: alpha
                    tasks: []
                    checks: []
                  - stage: mid
                    tasks: []
                    checks: []
                """,
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = project.Id,
            DefaultTemplateId = profileId,
            DefaultWorkflowProfileId = profileId,
            DefaultWorkflowProfileIdKey = profileId,
        });
        var issue = SeedIssue(db, project, "issue_quality_custom_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), workflowRunId);
        await SeedWorkflowRunAsync(db, workflowRunId, QualityRunState(workflowRunId, [
            ("zebra", [("zebra-ok", "Zebra ok", 0)]),
            ("alpha", [("alpha-repair", "Alpha repair", 1)]),
            ("mid", [("mid-ok", "Mid ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>()
            .GetQualityAsync(project.Id, now);

        Assert.Equal(["zebra", "alpha", "mid"], result.Window.Stages.Select(s => s.Stage));
        Assert.DoesNotContain(result.Window.Stages, stage =>
            stage.Stage is "plan" or "build" or "check" or "integrate");
        Assert.Equal([1, 1, 1], result.Window.Stages.Select(s => s.EnteredCount));
        Assert.Equal([0.0, 1.0, 0.0], result.Window.Stages.Select(s => s.ReworkRate));
    }

    [Fact]
    public async Task GetQualityAsync_BuiltinDefinitionOrder_PreservesStageMetrics()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-builtin-order-{Guid.NewGuid():N}", Name = "Quality Builtin Order" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_builtin_order_1", workflowRunId: "wr_quality_builtin_order_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_builtin_order_1");
        await SeedWorkflowRunAsync(db, "wr_quality_builtin_order_1", QualityRunState("wr_quality_builtin_order_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-repair", "Build repair", 1)]),
            ("check", [("check-ok", "Check ok", 0)]),
            ("integrate", [("integrate-ok", "Integrate ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>()
            .GetQualityAsync(project.Id, now);

        Assert.Equal(["plan", "build", "check", "integrate"], result.Window.Stages.Select(s => s.Stage));
        Assert.Equal([1, 1, 1, 1], result.Window.Stages.Select(s => s.EnteredCount));
        Assert.Equal([0.0, 1.0, 0.0, 0.0], result.Window.Stages.Select(s => s.ReworkRate));
    }
}
