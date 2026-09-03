using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.L1Tests.Specs.Runner.Api;

[Collection("LaunchIntegration")]
[Trait("level", "L1")]
public sealed class RunnerPollParentContextApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerPollParentContextApiSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WorkflowAgentJobResponseAddsOnlyCurrentParentTitleAndBody()
    {
        var projectId = $"runner-parent-context-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-parent-context-{Guid.NewGuid():N}";
        await SeedIssuesAndWorkflowAsync(projectId, workflowRunId);
        var issues = _fixture.Services.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        var response = await RunnerRoutes.ToWorkDispatchResponseAsync(
            new WorkDispatch(
                WorkflowRunId: workflowRunId,
                WorkId: "agent-work-1",
                WorkType: "agent-job",
                Stage: "plan",
                Title: "plan.1",
                Issue: new WorkIssueRef(projectId, 2),
                OwnerKind: WorkDispatchOwnerKinds.AgentJob,
                AgentJobId: "agent-job-1",
                ProjectId: projectId,
                ActionAttemptId: "plan.1"),
            issues.GetParentIssueContextAsync);

        var context = Assert.IsType<ParentIssueContextResponse>(response.ParentIssueContext);
        Assert.Equal("Current parent title", context.Title);
        Assert.Equal("Current parent body", context.Body);
        Assert.DoesNotContain("Parent-only comment", System.Text.Json.JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain("parent-only.txt", System.Text.Json.JsonSerializer.Serialize(response), StringComparison.Ordinal);
    }

    private async Task SeedIssuesAndWorkflowAsync(string projectId, string workflowRunId)
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "plan",
                [new TaskDefinition(
                    "plan",
                    "Plan",
                    "mohist/agent",
                    new Dictionary<string, JsonElement?>
                    {
                        ["name"] = JsonSerializer.SerializeToElement("mohist/planner"),
                        ["prompt"] = JsonSerializer.SerializeToElement("Original child task prompt"),
                    })],
                [])]);
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var parent = new DomainIssue
            {
                ProjectId = projectId,
                Number = 1,
                Title = "Current parent title",
                Body = "Current parent body",
                Status = IssueStatus.InProgress,
                Priority = "p2",
            };
            var child = new DomainIssue
            {
                ProjectId = projectId,
                Number = 2,
                Title = "Child title",
                Body = "Child body",
                Status = IssueStatus.InProgress,
                Priority = "p2",
                ParentIssueNumber = 1,
                WorkflowRunId = workflowRunId,
            };
            db.Issues.AddRange(
                new IssueRow
                {
                    ProjectId = projectId,
                    Number = 1,
                    State = IssueStore.Serialize(parent),
                },
                new IssueRow
                {
                    ProjectId = projectId,
                    Number = 2,
                    State = IssueStore.Serialize(child),
                    ParentIssueNumber = 1,
                });
            db.IssueComments.Add(new IssueCommentRow
            {
                Id = $"comment-{Guid.NewGuid():N}",
                ProjectId = projectId,
                IssueNumber = 1,
                Body = "Parent-only comment",
                CreatedAt = TestTime.UtcDateTime,
            });
            db.Attachments.Add(new AttachmentRow
            {
                Id = $"attachment-{Guid.NewGuid():N}",
                ProjectId = projectId,
                OwnerKind = "issue",
                OwnerIssueNumber = 1,
                OriginalFileName = "parent-only.txt",
                Size = 10,
                StoragePath = "/virtual/parent-only.txt",
                CreatedAt = TestTime.UtcDateTime,
            });
            var templateId = $"spec/parent-context-{Guid.NewGuid():N}";
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = templateId,
            });
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = templateId,
                Name = templateId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(definition),
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = WorkflowGrainTestHelpers.SerializeProfile(definition, templateId),
            });
            await db.SaveChangesAsync();
        }

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new(
            Name: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
             ProjectId: projectId,
             IssueNumber: 2),
            VerificationCommand: "true"));
    }
}
