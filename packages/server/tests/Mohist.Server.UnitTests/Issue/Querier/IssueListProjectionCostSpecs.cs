using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public sealed class IssueListProjectionCostSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueListProjectionCostSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListProjection_DoesNotAssembleCommentsAttachmentsOrHistory()
    {
        var project = new ProjectInfo
        {
            Id = $"proj-list-cost-{Guid.NewGuid():N}",
            Name = "List projection cost",
        };

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = new DomainIssue
        {
            ProjectId = project.Id,
            Number = 1,
            Title = "Summary issue",
            Body = new string('b', 4096),
            Status = IssueStatus.Backlog,
            Priority = "p2",
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = project.Id,
            Number = issue.Number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var counter = new SqlCommandCounter();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        var querier = new IssueQuerier(
            new CountingDbContextFactory(options),
            scope.ServiceProvider.GetRequiredService<ProjectQuerier>(),
            scope.ServiceProvider.GetRequiredService<ConfigService>(),
            scope.ServiceProvider.GetRequiredService<EffectiveWorkflowProfileResolver>(),
            scope.ServiceProvider.GetRequiredService<IWorkflowProfileProvider>(),
            scope.ServiceProvider.GetRequiredService<IssueReadModelLoader>());

        var baseline = await querier.ListWithLabelFiltersAsync(
            project.Id,
            project,
            stage: null,
            labels: null,
            priority: null,
            archived: null,
            all: null);
        var baselineCommandCount = counter.Count;
        Assert.Single(baseline);
        Assert.DoesNotContain(baseline[0].GetType().GetProperties(), property =>
            string.Equals(property.Name, "Body", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(counter.CommandTexts, ContainsDetailTable);

        for (var i = 0; i < 40; i++)
        {
            db.IssueComments.Add(new IssueCommentRow
            {
                Id = $"comment-list-cost-{i}",
                ProjectId = project.Id,
                IssueNumber = issue.Number,
                Body = new string('c', 2048),
                CreatedAt = TestTime.UtcDateTime,
            });
            db.Attachments.Add(new AttachmentRow
            {
                Id = $"attachment-list-cost-{i}",
                ProjectId = project.Id,
                OwnerKind = "issue",
                OwnerIssueNumber = issue.Number,
                OriginalFileName = $"attachment-{i}.txt",
                Size = 2048,
                StoragePath = $"/virtual/list-cost/{i}",
                CreatedAt = TestTime.UtcDateTime,
            });
            db.IssueEvents.Add(new IssueEventRow
            {
                Id = i + 1,
                Source = $"/mohist/projects/{project.Id}/issues/{issue.Number}",
                EventId = $"event-list-cost-{i}",
                Type = "com.mohist.issue.updated",
                Time = TestTime.UtcNow,
                SpecVersion = "1.0",
                DataContentType = "application/json",
                Data = JsonSerializer.SerializeToElement(new { projectId = project.Id, issueNumber = issue.Number }),
                ExtensionsJson = "{}",
            });
        }
        await db.SaveChangesAsync();

        counter.Reset();
        var withUnrelatedHistory = await querier.ListWithLabelFiltersAsync(
            project.Id,
            project,
            stage: null,
            labels: null,
            priority: null,
            archived: null,
            all: null);

        Assert.Single(withUnrelatedHistory);
        Assert.Equal(baselineCommandCount, counter.Count);
        Assert.DoesNotContain(counter.CommandTexts, ContainsDetailTable);
    }

    private static bool ContainsDetailTable(string commandText) =>
        commandText.Contains("IssueComments", StringComparison.OrdinalIgnoreCase)
        || commandText.Contains("Attachments", StringComparison.OrdinalIgnoreCase)
        || commandText.Contains("IssueEvents", StringComparison.OrdinalIgnoreCase);

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public List<string> CommandTexts { get; } = [];

        public void Reset()
        {
            Count = 0;
            CommandTexts.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            Count++;
            CommandTexts.Add(command.CommandText);
        }
    }

    private sealed class CountingDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public CountingDbContextFactory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
