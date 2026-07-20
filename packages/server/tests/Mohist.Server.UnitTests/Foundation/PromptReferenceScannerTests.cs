using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class PromptReferenceScannerTests
{
    private static WorkflowDefinition Parse(string yaml) => MohistWorkflow.ParseYaml(yaml);

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

    [Fact]
    public void Scan_RecoveryTask_ReturnsItsPromptKey()
    {
        var definition = Parse("""
        stages:
          - stage: build
            tasks:
              - id: review
                title: Review
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.review }}
                recovery:
                  budget: 1
                  handlers:
                    - when: output.promise=FAIL
                      tasks:
                        - id: recover:fix
                          title: Fix
                          uses: mohist/acp-agent
                          with:
                            prompt: ${{ prompts.auto-fix }}
                      retrySelf: true
            checks: []
        """);

        var keys = PromptReferenceScanner.Scan(definition);

        Assert.Contains("review", keys);
        Assert.Contains("auto-fix", keys);
    }

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
