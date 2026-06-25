using System.Reflection;
using Xunit;

namespace Mohist.Cli.Tests;

public class SourceCodeUpdaterStructureSpecs
{
    [Fact]
    public void Constructor_DependsOnCollaboratorsInsteadOfRawInfrastructureSet()
    {
        var constructor = typeof(SourceCodeUpdater)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Contains(typeof(UpdateOperations), parameterTypes);
        Assert.Contains(typeof(RuntimeConsistencyValidator), parameterTypes);
        Assert.Contains(typeof(ServiceReadinessProbe), parameterTypes);
        Assert.Contains(typeof(RunnerRefreshVerifier), parameterTypes);
        Assert.Contains(typeof(UpdateOutcomeReporter), parameterTypes);
        Assert.DoesNotContain(typeof(IServiceInstaller), parameterTypes);
        Assert.DoesNotContain(typeof(ICommandExecutor), parameterTypes);
        Assert.DoesNotContain(typeof(IFileSystem), parameterTypes);
        Assert.DoesNotContain(typeof(IEnvironmentVariableProvider), parameterTypes);
        Assert.DoesNotContain(typeof(HttpClient), parameterTypes);
        Assert.True(parameterTypes.Length < 12, $"Expected fewer than 12 constructor parameters, got {parameterTypes.Length}.");
    }
}
