using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("LaunchIntegration")]
public sealed class RunnerPollParentContextApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerPollParentContextApiSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PollAddsOnlyCurrentParentTitleAndBodyToEligiblePlanDispatch()
    {
        var projectId = $"runner-parent-context-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-parent-context-{Guid.NewGuid():N}";
        var runnerId = $"runner-parent-context-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedIssuesAndWorkflowAsync(projectId, workflowRunId);
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["mohist/*"], "test-host", projectId));

            using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dispatch = await response.ReadFirstDispatchElementAsync()
                ?? throw new InvalidOperationException("Expected a dispatch from /poll");
            var context = dispatch.GetProperty("parentIssueContext");
            Assert.Equal(["body", "title"], context.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal("Current parent title", context.GetProperty("title").GetString());
            Assert.Equal("Current parent body", context.GetProperty("body").GetString());
            Assert.DoesNotContain("Parent-only comment", dispatch.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("parent-only.txt", dispatch.GetRawText(), StringComparison.Ordinal);

            using var withJson = JsonDocument.Parse(dispatch.GetProperty("with").GetString()!);
            Assert.Equal("Original child task prompt", withJson.RootElement.GetProperty("prompt").GetString());
            Assert.False(withJson.RootElement.TryGetProperty("parentIssueContext", out _));
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    private async Task SeedIssuesAndWorkflowAsync(string projectId, string workflowRunId)
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "plan",
                [new TaskDefinition(
                    "plan",
                    "Plan",
                    "mohist/opencode",
                    new Dictionary<string, JsonElement?>
                    {
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
             IssueNumber: 2)));
    }
}
