using System.CommandLine;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;

namespace Mohist.Cli.TestSupport;

public static class CliHelpTestSupport
{
    public static string Render(string[] args)
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var commands = new RejectingCommandExecutor();
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(CreateRejectingHttpClient(), TextWriter.Null, TextWriter.Null, files, commands));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(files);
        services.AddSingleton<ICommandExecutor>(commands);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton<IServiceInstaller>(sp => new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, files, sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton(sp => new UpdateOperations(TextWriter.Null, TextWriter.Null, sp.GetRequiredService<IServiceInstaller>(), sp.GetRequiredService<ICommandExecutor>(), files, environment));
        services.AddSingleton(new RuntimeConsistencyValidator(CreateRejectingHttpClient(), commands, files, environment, TextWriter.Null));
        services.AddSingleton(new ServiceReadinessProbe(CreateRejectingHttpClient(), TextWriter.Null));
        services.AddSingleton(new RunnerRefreshVerifier(CreateRejectingHttpClient(), commands, files));
        services.AddSingleton(new UpdateOutcomeReporter(CreateRejectingHttpClient(), TextWriter.Null));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(new SkillAssetService(files, environment, new SkillAssetRootResolver(
            files,
            environment,
            getOverrideAssetRoot: () => "/assets",
            getManagedAssetRoot: null,
            getUserHome: () => "/home/test")));
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();
        services.AddSingleton<SkillInstallService>(_ => new SkillInstallService(
            _.GetRequiredService<SkillAssetService>(),
            _.GetRequiredService<IFileSystem>(),
            _.GetRequiredService<IEnvironmentVariableProvider>(),
            TextWriter.Null,
            TextWriter.Null));

        var provider = services.BuildServiceProvider();
        var root = MohistCliCommands.Build(provider.GetRequiredService<MohistCliApi>(), provider);
        using var writer = new StringWriter();
        root.Parse(args).Invoke(new InvocationConfiguration { Output = writer, Error = writer });
        return writer.ToString();
    }

    private static HttpClient CreateRejectingHttpClient() =>
        new(new RejectingHttpHandler()) { BaseAddress = new Uri("http://localhost:3456") };

    private sealed class RejectingCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName,
            string[] args,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"Unexpected command: {fileName}");
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
    }
}
