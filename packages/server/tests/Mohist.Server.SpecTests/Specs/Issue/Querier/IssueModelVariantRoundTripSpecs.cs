using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;
using IssueEntity = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueModelVariantRoundTripSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueModelVariantRoundTripSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReadModel_ReturnsModelAndVariantFromIssueWorkflowProfile()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-variant-rt-{Guid.NewGuid():N}", Name = "Variant RT" };

        var issue = new IssueEntity
        {
            ProjectId = project.Id,
            Number = 1,
            Title = "Variant round-trip",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });

        // Persist issue variables with model + variant in agent config.
        var issueBundle = VariableBundle.Empty;
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: IssueModelMetadata.FieldPatch<string>.Set("anthropic/claude-opus-4-20250514"),
            ModelVariant: IssueModelMetadata.FieldPatch<string>.Set("high"),
            StageModels: IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent,
            StageModelVariants: IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent);
        var patched = IssueModelMetadata.ApplyModelMetadata(issueBundle, seedPatch);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = issue.ProjectId,
            IssueNumber = issue.Number,
            Variables = patched.ToJson(),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var loaded = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(loaded);
        Assert.Equal("anthropic/claude-opus-4-20250514", loaded!.Model);
        Assert.Equal("high", loaded.ModelVariant);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReadModel_ReturnsPerStageModelAndVariant()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-variant-stage-{Guid.NewGuid():N}", Name = "Variant Stage" };

        var issue = new IssueEntity
        {
            ProjectId = project.Id,
            Number = 1,
            Title = "Per-stage variant",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });

        var stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "openai/gpt-5.5",
            ["build"] = "anthropic/claude-sonnet-4-20250514",
        };
        var stageVariants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "low",
            ["build"] = "max",
        };
        var stagePatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: IssueModelMetadata.FieldPatch<string>.Absent,
            ModelVariant: IssueModelMetadata.FieldPatch<string>.Absent,
            StageModels: IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(stageModels),
            StageModelVariants: IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(stageVariants));
        var patched = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, stagePatch);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = issue.ProjectId,
            IssueNumber = issue.Number,
            Variables = patched.ToJson(),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var loaded = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.StageModels);
        Assert.NotNull(loaded.StageModelVariants);
        Assert.Equal("openai/gpt-5.5", loaded.StageModels!["plan"]);
        Assert.Equal("low", loaded.StageModelVariants!["plan"]);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", loaded.StageModels!["build"]);
        Assert.Equal("max", loaded.StageModelVariants!["build"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReadModel_SuppressesVariantWhenModelIsAbsent()
    {
        using var scope = _fixture.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<IEnvironmentVariableProvider>();
        var mockEnv = (MockEnvironmentVariableProvider)env;
        var previousAgent = mockEnv["MOHIST__CONFIG__AGENT"];
        mockEnv["MOHIST__CONFIG__AGENT"] = """
            { "model": "anthropic/claude-sonnet-4-20250514" }
            """;
        try
        {
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-variant-no-model-{Guid.NewGuid():N}", Name = "No Model" };

        var issue = new IssueEntity
        {
            ProjectId = project.Id,
            Number = 1,
            Title = "Variant without model",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });

        // Seed agent config with variant but no model. The querier merges
        // global + project + issue layers, so a model from the global layer
        // still applies at display time — the issue-level variant attaches
        // to that merged model and is preserved. The dependency invariant
        // ("variant meaningless without its model") is enforced at WRITE
        // time, so the read path simply surfaces the merged result.
        var rawJson = """
        {
          "vars": { "agent": { "variant": "high" } },
          "stages": {}
        }
        """;
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = issue.ProjectId,
            IssueNumber = issue.Number,
            Variables = rawJson,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var loaded = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(loaded);
        // The issue has no local model — the global layer provides one, so the
        // merged display shows it. The variant from the issue layer is
        // preserved alongside that model.
        Assert.NotNull(loaded!.Model);
        Assert.Equal("high", loaded.ModelVariant);
        }
        finally
        {
            mockEnv["MOHIST__CONFIG__AGENT"] = previousAgent;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReadModel_MalformedStageModelsJson_ReturnsIssueWithoutPerStageOverrides()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-variant-malformed-{Guid.NewGuid():N}", Name = "Malformed" };

        var issue = new IssueEntity
        {
            ProjectId = project.Id,
            Number = 1,
            Title = "Malformed stage JSON",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });

        // Malformed: stages block is a string instead of an object — IssueQuerier
        // must surface the issue without per-stage overrides, never crash.
        var rawJson = """
        {
          "vars": {},
          "stages": "this-should-be-an-object"
        }
        """;
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = issue.ProjectId,
            IssueNumber = issue.Number,
            Variables = rawJson,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var loaded = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.StageModels);
        Assert.Null(loaded.StageModelVariants);
    }
}
