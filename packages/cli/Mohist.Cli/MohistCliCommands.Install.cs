using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class InstallCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var install = new Command("install", "Install mohist components from source");
        var installer = provider.GetRequiredService<IServiceInstaller>();
        var api = provider.GetRequiredService<MohistCliApi>();

        install.Subcommands.Add(BuildServerInstall(installer));
        install.Subcommands.Add(BuildRunnerInstall(installer, api));
        install.Subcommands.Add(BuildSlackInstall(installer));

        return install;
    }

    private static Command BuildServerInstall(IServiceInstaller installer)
    {
        var cmd = new Command("server", "Install server as a managed background service from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var listenUrlOpt = new Option<string?>("--listen-url") { Description = "Server listen URL" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(listenUrlOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var listenUrl = ctx.GetValue(listenUrlOpt);
            return installer.InstallServerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, listenUrl, null, null));
        });
        return cmd;
    }

    private static Command BuildRunnerInstall(IServiceInstaller installer, MohistCliApi api)
    {
        var cmd = new Command("runner", "Install runner as a managed background service from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var serverUrlOpt = new Option<string?>("--server-url") { Description = "Server URL" };
        var runnerRootOpt = new Option<string?>("--runner-root") { Description = "Runner root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(serverUrlOpt);
        cmd.Options.Add(runnerRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var serverUrl = ctx.GetValue(serverUrlOpt);
            var runnerRoot = ctx.GetValue(runnerRootOpt);

            // The enrollment token is minted by the server the CLI is
            // authenticated against (admin) and injected into the runner
            // service environment; the runner exchanges it for its own
            // machine credential on first start. Dry runs stay offline.
            string? enrollmentToken = null;
            if (!dryRun)
            {
                enrollmentToken = await FetchEnrollmentTokenAsync(api);
                if (enrollmentToken is null)
                    return 1;
            }

            return await installer.InstallRunnerAsync(
                new ServiceInstallOptions(dryRun, unitDir, repoRoot, null, serverUrl, runnerRoot, enrollmentToken));
        });
        return cmd;
    }

    private static async Task<string?> FetchEnrollmentTokenAsync(MohistCliApi api)
    {
        try
        {
            var data = await api.PostDataAsync("/api/runners/enrollment-tokens", new { }).ConfigureAwait(false);
            var token = data?["token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token))
            {
                api.Error.WriteLine("Server returned no runner enrollment token.");
                return null;
            }

            return token;
        }
        catch (MohistCliApi.ApiResponseException ex)
        {
            var code = string.IsNullOrWhiteSpace(ex.Code) ? $"http-{(int)ex.StatusCode}" : ex.Code;
            api.Error.WriteLine($"Enrollment token request failed: {ex.Message} (code={code})");
            return null;
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return null;
        }
    }

    private static Command BuildSlackInstall(IServiceInstaller installer)
    {
        var cmd = new Command("slack", "Install the mohist-slack adapter as a managed background service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var serverUrlOpt = new Option<string?>("--server-url") { Description = "Server URL" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(serverUrlOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx => installer.InstallSlackAsync(new ServiceInstallOptions(
            ctx.GetValue(dryRunOpt), ctx.GetValue(unitDirOpt), ctx.GetValue(repoRootOpt), null,
            ctx.GetValue(serverUrlOpt), null)));
        return cmd;
    }
}
