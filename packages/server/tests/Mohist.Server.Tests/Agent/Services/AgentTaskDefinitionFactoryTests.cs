using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Tests.Agent.Services;

[Trait("level", "L0")]
public sealed class AgentTaskDefinitionFactoryTests
{
    private static readonly ExecutionConfigHint Default = new("pi", "provider/default", "balanced");

    [Fact]
    public void DerivesFirstSentence_WithUnicodeLetters_AndCapsName()
    {
        var prompt = "Überprüfe die Abhängigkeiten und repariere den Fehler. Danach dokumentiere alles.";

        var definition = AgentTaskDefinitionFactory.Build(
            prompt,
            hasAcceptedAttachment: false,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: Default,
            identity: "project\nkey",
            occupiedNames: []);

        Assert.StartsWith("Überprüfe die Abhängigkeiten", definition.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("Danach", definition.Name, StringComparison.Ordinal);
        Assert.InRange(definition.Name.EnumerateRunes().Count(), 1, AgentTaskDefinitionFactory.NameLengthCap);
    }

    [Fact]
    public void DerivesFirstSentence_WithUnicodeSentenceBoundary()
    {
        var definition = AgentTaskDefinitionFactory.Build(
            "修复登录流程。然后补充测试。",
            hasAcceptedAttachment: false,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: null,
            identity: "project\\njapanese",
            occupiedNames: []);

        Assert.Equal("修复登录流程", definition.Name);
    }

    [Fact]
    public void DisambiguatesNames_CaseInsensitively_AndIncludesReservedNames()
    {
        var definition = AgentTaskDefinitionFactory.Build(
            "Base",
            hasAcceptedAttachment: false,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: null,
            identity: "project\nkey",
            occupiedNames: ["base", "Base 2"]);

        Assert.Equal("Base 3", definition.Name);

        var reserved = AgentTaskDefinitionFactory.Build(
            "mohist-slack",
            hasAcceptedAttachment: false,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: null,
            identity: "project\nreserved",
            occupiedNames: []);

        Assert.Equal("mohist-slack 2", reserved.Name);
    }

    [Fact]
    public async Task CreateAsync_TerminalRejectionArchivedName_RemainsOccupiedForNewIdentity()
    {
        await using var database = TestSqliteDatabase.CreateModelSchema();
        const string projectId = "project-terminal-rejection";
        const string archivedName = "Terminal rejection task";
        await using (var db = database.CreateContext())
        {
            db.Agents.Add(new AgentRow
            {
                Id = "agent-terminal-rejection",
                ProjectId = projectId,
                Name = archivedName,
                Status = AgentStatus.Archived,
                State = AgentStore.Serialize(new DomainAgent
                {
                    Id = "agent-terminal-rejection",
                    ProjectId = projectId,
                    Name = archivedName,
                    Status = AgentStatus.Archived,
                }),
            });
            await db.SaveChangesAsync();
        }

        var dbFactory = new TestDbContextFactory(database.Options);
        var agents = new AgentQuerier(dbFactory);
        var archived = Assert.Single(await agents.ListAsync(projectId, all: true));
        Assert.Equal(AgentStatus.Archived, archived.Status);

        var factory = new AgentTaskDefinitionFactory(
            agents,
            new ProjectDefaultExecutionConfigReader(dbFactory));
        var derived = await factory.CreateAsync(
            projectId,
            prompt: archivedName,
            hasAcceptedAttachment: false,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            identity: "task-terminal-rejection-derived-retry");

        Assert.Equal("Terminal rejection task 2", derived.Name);
    }

    [Fact]
    public void AttachmentOnlyTask_UsesDeterministicShortToken()
    {
        var first = AgentTaskDefinitionFactory.Build(
            prompt: null,
            hasAcceptedAttachment: true,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: null,
            identity: "project\nattachment-key",
            occupiedNames: []);
        var second = AgentTaskDefinitionFactory.Build(
            prompt: null,
            hasAcceptedAttachment: true,
            nameHint: null,
            callerHint: new ExecutionConfigHint(Model: "provider/task"),
            projectDefault: null,
            identity: "project\nattachment-key",
            occupiedNames: []);

        Assert.Equal(first.Name, second.Name);
        Assert.StartsWith("Task ", first.Name, StringComparison.Ordinal);
        Assert.Equal("Created from attachments", first.Description);
        Assert.NotEmpty(first.Instructions);
    }

    [Fact]
    public void IdenticalRequests_ProduceIdenticalDefinitions_AndMaterializedConfig()
    {
        var first = BuildTask();
        var second = BuildTask();

        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Description, second.Description);
        Assert.Equal(first.Instructions, second.Instructions);
        Assert.Equal(first.AgentConfig.GetRawText(), second.AgentConfig.GetRawText());
        Assert.Null(AgentConfigSchema.Validate(first.AgentConfig));
        Assert.Equal("pi", first.AgentConfig.GetProperty("runtime").GetString());
        Assert.Equal("provider/task", first.AgentConfig.GetProperty("model").GetString());
        Assert.Equal("high", first.AgentConfig.GetProperty("variant").GetString());
    }

    [Fact]
    public void MissingModel_RejectsBeforeDefinitionCanBeCreated()
    {
        var exception = Assert.Throws<AgentTaskDefinitionExecutionConfigException>(() =>
            AgentTaskDefinitionFactory.Build(
                "No model yet",
                hasAcceptedAttachment: false,
                nameHint: null,
                callerHint: null,
                projectDefault: null,
                identity: "project\nmissing",
                occupiedNames: []));

        Assert.Contains("Supply runtime/model/variant hints", exception.Message, StringComparison.Ordinal);
        Assert.Contains("configure the Project default", exception.Message, StringComparison.Ordinal);
    }

    private static AgentTaskDefinition BuildTask() => AgentTaskDefinitionFactory.Build(
        "Implement the task\nwith stable instructions.",
        hasAcceptedAttachment: false,
        nameHint: null,
        callerHint: new ExecutionConfigHint(Model: "provider/task", Variant: "high"),
        projectDefault: Default,
        identity: "project\ndeterministic",
        occupiedNames: []);
}

[Trait("level", "L0")]
public sealed class AgentTaskLaunchFingerprintTests
{
    [Fact]
    public void NoHints_KeepThePreHintCanonicalFingerprint()
    {
        var baseline = new AgentLaunchCoordinatorRequest(
            "task", null, null, null, null, null, null, null);
        var explicitNulls = baseline with { Model = null, Variant = null };

        Assert.Equal(
            AgentLaunchCoordinatorCodec.Fingerprint(baseline),
            AgentLaunchCoordinatorCodec.Fingerprint(explicitNulls));
    }

    [Fact]
    public void ModelAndVariantHintChanges_AreVisibleToReplayFingerprint()
    {
        var baseline = new AgentLaunchCoordinatorRequest(
            "task", null, null, null, null, null, null, null);
        var model = baseline with { Model = "provider/model" };
        var changedModel = baseline with { Model = "provider/other" };
        var variant = baseline with { Variant = "high" };
        var changedVariant = baseline with { Variant = "low" };
        var modelRemoved = model with { Model = null };
        var variantRemoved = variant with { Variant = null };

        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(baseline), AgentLaunchCoordinatorCodec.Fingerprint(model));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(model), AgentLaunchCoordinatorCodec.Fingerprint(changedModel));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(baseline), AgentLaunchCoordinatorCodec.Fingerprint(variant));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(variant), AgentLaunchCoordinatorCodec.Fingerprint(changedVariant));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(model), AgentLaunchCoordinatorCodec.Fingerprint(modelRemoved));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(variant), AgentLaunchCoordinatorCodec.Fingerprint(variantRemoved));
        Assert.NotEqual(AgentLaunchCoordinatorCodec.Fingerprint(model), AgentLaunchCoordinatorCodec.Fingerprint(baseline));
    }
}
