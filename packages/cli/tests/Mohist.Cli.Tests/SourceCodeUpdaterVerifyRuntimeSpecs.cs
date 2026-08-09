using System.Net;
using System.Reflection;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class SourceCodeUpdaterVerifyRuntimeSpecs
{
    [Fact]
    public async Task VerifyRuntime_DoesNotDowngradeActivationIdentityToWarning()
    {
        var handler = new RecordingHttpHandler((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/system/info" => Task.FromResult(Json("{\"running\":{\"gitHash\":\"abcdef0\"},\"services\":{\"runner\":\"active\"}}")),
                "/" => Task.FromResult(Html("<html><script src=\"/assets/app.js\"></script></html>")),
                "/assets/app.js" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("// bundle") }),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            };
        });
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 0, "mo 1.2.3\n");
        var files = new FakeFileSystem();
        var skillRoot = "/home/test/.mohist/cli/skill-data/runtime-check";
        files.CreateDirectory(skillRoot);
        files.AddFile(Path.Combine(skillRoot, "SKILL.md"), "# Skill");
        var output = new StringWriter();
        var error = new StringWriter();
        var updater = BuildUpdater(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") }, output, error, commands, files);
        var context = new UpdateContext(false, "/clean", "/usr/bin/mo", CancellationToken.None)
        {
            SourceHead = "abcdef0",
        };

        var exitCode = await InvokeVerifyRuntimeStageAsync(updater, context);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateOutcome.Ready, context.Outcome);
        var text = output.ToString();
        Assert.Contains("Runner connection", text);
        Assert.DoesNotContain("Server identity", text);
        Assert.DoesNotContain("Runner identity", text);
    }

    [Fact]
    public async Task VerifyRuntime_DryRunStatesThatActivationOwnsRuntimeIdentity()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var output = new StringWriter();
        var updater = BuildUpdater(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") }, output, new StringWriter());
        var context = new UpdateContext(true, "/clean", "/usr/bin/mo", CancellationToken.None);

        var exitCode = await InvokeVerifyRuntimeStageAsync(updater, context);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("activation verifies server and runner identities", output.ToString());
    }

    private static SourceCodeUpdater BuildUpdater(
        HttpClient http,
        StringWriter output,
        StringWriter error,
        ICommandExecutor? commands = null,
        FakeFileSystem? files = null)
    {
        var executor = commands ?? new FakeCommandExecutor();
        var fileSystem = files ?? new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        var operations = new UpdateOperations(output, error, new FakeServiceInstaller(), executor, fileSystem, environment, getUserHome: () => "/home/test");
        var validator = new RuntimeConsistencyValidator(http, executor, fileSystem, environment, output, getUserHome: () => "/home/test");
        return new SourceCodeUpdater(
            output,
            error,
            operations,
            validator,
            new ServiceReadinessProbe(http, output),
            new RunnerRefreshVerifier(http, executor, fileSystem),
            new UpdateOutcomeReporter(http, output));
    }

    private static async Task<int> InvokeVerifyRuntimeStageAsync(SourceCodeUpdater updater, UpdateContext context)
    {
        var method = typeof(SourceCodeUpdater).GetMethod("VerifyRuntimeStageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<int>)method.Invoke(updater, [context, CancellationToken.None])!;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
    };
}
