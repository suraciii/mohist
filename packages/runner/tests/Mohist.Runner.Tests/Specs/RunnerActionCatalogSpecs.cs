using Microsoft.Extensions.DependencyInjection;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class RunnerActionCatalogSpecs
{
    [Theory]
    [InlineData("core/process")]
    [InlineData("core/script")]
    [InlineData("core/artifact-exists")]
    [InlineData("core/marker")]
    [InlineData("mohist/agent")]
    [InlineData("mohist/check/ai-review")]
    [InlineData("mohist/openspec-tasks")]
    [InlineData("mohist/merge-ready")]
    [InlineData("mohist/rebase")]
    [InlineData("mohist/rebase-status")]
    [InlineData("mohist/openspec-sync")]
    [InlineData("mohist/archive-change")]
    [InlineData("mohist/merge")]
    public void RegisterDefaults_ExposesCoreActionsAndMohistActions(string uses)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAgentExecutor, FakeAgentExecutor>()
            .AddSingleton<ISessionTelemetrySink, NullSessionTelemetrySink>()
            .AddSingleton<IAgentCompletionVerifier, AgentCompletionVerifier>()
            .AddSingleton<IAgentSessionRepairer, NoopAgentSessionRepairer>()
            .BuildServiceProvider();
        var manager = new ActionManager(services, SpecHelpers.Logger<ActionManager>());

        RunnerActionCatalog.RegisterDefaults(manager, services);

        Assert.True(manager.HasAction(uses));
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request) =>
            Task.FromResult(new AgentExecutionResult(0));
    }
}
