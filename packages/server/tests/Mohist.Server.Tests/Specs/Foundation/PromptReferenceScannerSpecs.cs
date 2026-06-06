using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Foundation;

public class PromptReferenceScannerSpecs
{
    private static WorkflowDefinition Parse(string yaml) => MohistWorkflow.ParseYaml(yaml);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_TaskWithPromptReference_ReturnsTopLevelKey()
    {
        var definition = Parse("""
        stages:
          - stage: plan
            tasks:
              - id: write-proposal
                title: Write proposal
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.proposal }}
            checks: []
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Equal(new[] { "proposal" }, keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_CheckWithPromptReference_ReturnsTopLevelKey()
    {
        var definition = Parse("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: review
                title: Review
                uses: mohist/ai-review
                with:
                  prompt: ${{ prompts.review }}
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Equal(new[] { "review" }, keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_RepairAndVerifyTasks_ReturnTheirPromptKeys()
    {
        var definition = Parse("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: review
                title: Review
                uses: mohist/ai-review
                with:
                  prompt: ${{ prompts.review }}
                repairLimit: 1
                repairTask:
                  id: fix
                  title: Fix
                  uses: mohist/acp-agent
                  with:
                    prompt: ${{ prompts.auto-fix }}
                verifyTask:
                  id: verify
                  title: Verify
                  uses: mohist/acp-agent
                  with:
                    prompt: ${{ prompts.re-verify }}
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Contains("review", keys);
        Assert.Contains("auto-fix", keys);
        Assert.Contains("re-verify", keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_DuplicateReferences_ReturnUniqueSet()
    {
        var definition = Parse("""
        stages:
          - stage: plan
            tasks:
              - id: t1
                title: T1
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.proposal }}
              - id: t2
                title: T2
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.proposal }}
            checks: []
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Single(keys);
        Assert.Contains("proposal", keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_KeyWithHyphensAndUnderscores_MatchesAllowedIdentifierCharacters()
    {
        var definition = Parse("""
        stages:
          - stage: plan
            tasks:
              - id: t1
                title: T1
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.deploy_checklist }}
              - id: t2
                title: T2
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.build-task }}
            checks: []
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Contains("deploy_checklist", keys);
        Assert.Contains("build-task", keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void Scan_NestedObjectValue_StillMatchesPromptReference()
    {
        var definition = Parse("""
        stages:
          - stage: plan
            tasks:
              - id: t1
                title: T1
                uses: mohist/acp-agent
                with:
                  options:
                    prompt: ${{ prompts.proposal }}
            checks: []
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Contains("proposal", keys);
    }
}
