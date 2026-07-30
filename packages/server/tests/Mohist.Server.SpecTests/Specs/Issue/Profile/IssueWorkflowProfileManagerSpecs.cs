using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class IssueWorkflowProfileManagerSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_profile";
    private readonly TestSqliteDatabase _database;
    private readonly IssueWorkflowProfileManager _manager;

    public IssueWorkflowProfileManagerSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _manager = new IssueWorkflowProfileManager(new TestDbContextFactory(_database.Options), NullActionCatalogSource.Instance);

        using var db = new MohistDbContext(_database.Options);
        var issueNumbers = new[] { 1, 2, 4, 5 };
        foreach (var issueNumber in issueNumbers)
        {
            db.Issues.Add(new IssueRow
            {
                State = JSON.Serialize(new
                {
                    projectId = ProjectId,
                    number = issueNumber,
                }),
            });
        }
        db.SaveChanges();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    // ===================== Template =====================

    [Fact]
    public async Task GetProfile_ReturnsNull_WhenNoRecord()
    {
        var profile = await _manager.GetProfileAsync(ProjectId, 99);
        Assert.Null(profile);
    }

    [Fact]
    public async Task UpdateTemplate_ProjectReference_StoresSourceTemplateId()
    {
        var result = await _manager.UpdateTemplateAsync(ProjectId, 1,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-template"));

        Assert.Equal(ProjectId, result.State.ProjectId);
        Assert.Equal(1, result.State.IssueNumber);
        Assert.Equal("some-template", result.State.SourceTemplateId);
        Assert.Null(result.State.Template);

        var stored = await _manager.GetProfileAsync(ProjectId, 1);
        Assert.Equal(ProjectId, stored!.ProjectId);
        Assert.Equal(1, stored.IssueNumber);
    }

    [Fact]
    public async Task UpdateTemplate_CustomYaml_StoresParsedDefinition()
    {
        var yaml = """
            id: my-custom
            stages:
              - stage: build
                tasks: []
                checks: []
            """;
        var result = await _manager.UpdateTemplateAsync(ProjectId, 2,
            new IssueTemplateUpdateRequest(Template: yaml));

        Assert.Null(result.State.SourceTemplateId);
        Assert.NotNull(result.State.Template);
        Assert.Equal(ActionValidationStatus.Skipped, result.ActionValidation);

        var state = await _manager.GetStateAsync(ProjectId, 2);
        Assert.NotNull(state.Template);
        Assert.Equal("my-custom", state.Template!.Id);
    }

    [Fact]
    public async Task UpdateTemplate_BothSet_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpdateTemplateAsync(ProjectId, 3,
                new IssueTemplateUpdateRequest(
                    ProjectTemplateId: "t1",
                    Template: """
                        id: foo
                        stages:
                          - stage: s
                            tasks: []
                            checks: []
                        """)));
    }

    [Fact]
    public async Task UpdateTemplate_NullClears_BothFields()
    {
        // first set
        await _manager.UpdateTemplateAsync(ProjectId, 4,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "t1"));
        // then clear
        var result = await _manager.UpdateTemplateAsync(ProjectId, 4,
            new IssueTemplateUpdateRequest());

        Assert.Null(result.State.SourceTemplateId);
        Assert.Null(result.State.Template);
    }

    [Fact]
    public async Task UpdateTemplate_OverwritesPreviousCustom()
    {
        var yaml1 = """
            id: v1
            stages:
              - stage: s1
                tasks: []
                checks: []
            """;
        var yaml2 = """
            id: v1
            stages:
              - stage: s2
                tasks: []
                checks: []
            """;
        await _manager.UpdateTemplateAsync(ProjectId, 5, new IssueTemplateUpdateRequest(Template: yaml1));
        await _manager.UpdateTemplateAsync(ProjectId, 5, new IssueTemplateUpdateRequest(Template: yaml2));

        var def = await _manager.GetTemplateAsync(ProjectId, 5);
        Assert.Single(def!.Stages);
        Assert.Equal("s2", def.Stages[0].Stage);
    }

}
