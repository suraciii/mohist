using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow;

public class WorkflowYamlSerializerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_ParsesDeclaredOutputs()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
                outputs:
                  - name: openspecName
                    from: output.openspecName
                  - name: changeDir
                    from: output.changeDir
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Outputs);
        Assert.Equal(2, task.Outputs!.Count);
        Assert.Equal("openspecName", task.Outputs[0].Name);
        Assert.Equal("output.openspecName", task.Outputs[0].From);
        Assert.Equal("changeDir", task.Outputs[1].Name);
        Assert.Equal("output.changeDir", task.Outputs[1].From);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_Omitted_IsValid()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Null(task.Outputs);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_MissingName_Throws()
    {
        var yaml = """
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
                outputs:
                  - from: output.openspecName
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("'name'", ex.Message);
        Assert.Contains("proposal", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_MissingFrom_Throws()
    {
        var yaml = """
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
                outputs:
                  - name: openspecName
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("'from'", ex.Message);
        Assert.Contains("proposal", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_DuplicateName_Throws()
    {
        var yaml = """
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
                outputs:
                  - name: openspecName
                    from: output.openspecName
                  - name: openspecName
                    from: output.alternative
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(yaml));
        Assert.Contains("duplicate output name", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openspecName", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskOutputs_RoundTripsThroughYaml()
    {
        var original = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: Write proposal
                outputs:
                  - name: openspecName
                    from: output.openspecName
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(original);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        var originalTask = original.Stages.Single().Tasks.Single();
        var reparsedTask = reparsed.Stages.Single().Tasks.Single();
        Assert.Equal(originalTask.Outputs, reparsedTask.Outputs);
        Assert.Contains("outputs:", yaml);
        Assert.Contains("name: openspecName", yaml);
        Assert.Contains("from: output.openspecName", yaml);
    }
}
