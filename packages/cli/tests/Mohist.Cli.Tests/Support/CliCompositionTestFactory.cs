using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;

namespace Mohist.Cli.Tests.Support;

internal static class CliCompositionTestFactory
{
    public static CliComposition Create(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        SkillAssetService skillAssets,
        TextWriter output,
        TextWriter error,
        HttpClient? http = null,
        ICommandExecutor? commandExecutor = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null,
        Func<string?>? getLocalHostname = null)
    {
        commandExecutor ??= new NoopCommandExecutor();
        http ??= new HttpClient(new RejectingHttpMessageHandler())
        {
            BaseAddress = new Uri("http://fake.invalid"),
        };
        timeProvider ??= new FakeTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        if (pollWait is null)
        {
            if (timeProvider is not FakeTimeProvider fakeTimeProvider)
            {
                throw new ArgumentException(
                    "A deterministic pollWait is required with a non-fake time provider.",
                    nameof(pollWait));
            }

            pollWait = (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                fakeTimeProvider.Advance(delay);
                return Task.CompletedTask;
            };
        }
        getLocalHostname ??= () => "mohist-test-host";

        return CliComposition.Create(new CliCompositionOptions(
            Http: http,
            Output: output,
            Error: error,
            FileSystem: fileSystem,
            CommandExecutor: commandExecutor,
            Environment: environment,
            StandardInput: TextReader.Null,
            Installer: new FakeServiceInstaller(),
            Updater: new FakeSourceCodeUpdater(),
            SkillAssets: skillAssets,
            GetUserHome: () => "/mohist-tests/user",
            GetLocalHostname: getLocalHostname,
            TimeProvider: timeProvider,
            PollWait: pollWait));
    }

    private sealed class RejectingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Unexpected HTTP request: {request.Method} {request.RequestUri}");
    }
}
