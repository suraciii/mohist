using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillsCliRuntimeSpecs
{
    [Fact]
    public async Task CliProgram_Main_RendersSkillsHelp_WithoutDependencyInjectionFailures()
    {
        var (exitCode, stdout, stderr) = await InvokeProcessAsync("skills --help");

        Assert.True(exitCode == 0, $"stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Contains("Manage coder agent skills", stdout);
        Assert.Contains("install", stdout);
        Assert.Contains("list", stdout);
        Assert.Contains("get", stdout);
        Assert.Contains("path", stdout);
        Assert.DoesNotContain("Unable to resolve service for type 'System.IO.TextWriter'", stderr);
    }

    [Fact]
    public async Task CliProgram_Main_CanExecuteReadOnlySkillsCommand_ThroughRealCompositionPath()
    {
        var (exitCode, stdout, stderr) = await InvokeMainAsync(["skills", "get", "mohist"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("name: mohist", stdout);
        Assert.DoesNotContain("mo skills get mohist --full", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task PublishedCli_ContainsPackagedSkillData_ForGetAndHermesInstall()
    {
        var repoRoot = GetRepoRoot();
        var publishRoot = Path.Combine(Path.GetTempPath(), $"mohist-cli-publish-{Guid.NewGuid():N}");
        var workRoot = Path.Combine(Path.GetTempPath(), $"mohist-cli-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishRoot);
        Directory.CreateDirectory(workRoot);

        try
        {
            var publish = await RunProcessAsync(
                "dotnet",
                "publish packages/cli/Mohist.Cli -c Release -o \"" + publishRoot + "\"",
                repoRoot,
                []);

            Assert.True(publish.ExitCode == 0, $"publish failed\nstdout:\n{publish.Stdout}\n\nstderr:\n{publish.Stderr}");
            Assert.True(File.Exists(Path.Combine(publishRoot, "skill-data", "mohist", "SKILL.md")));

            var hermesHome = Path.Combine(workRoot, "hermes-home");
            var cliDll = Path.Combine(publishRoot, "Mohist.Cli.dll");

            var get = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills get mohist",
                workRoot,
                [("MOHIST_SKILLS_DIR", null), ("HERMES_HOME", hermesHome)]);

            Assert.True(get.ExitCode == 0, $"get failed\nstdout:\n{get.Stdout}\n\nstderr:\n{get.Stderr}");
            Assert.Contains("name: mohist", get.Stdout);
            Assert.DoesNotContain("mo skills get mohist --full", get.Stdout, StringComparison.Ordinal);

            var install = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills install --hermes",
                workRoot,
                [("MOHIST_SKILLS_DIR", null), ("HERMES_HOME", hermesHome)]);

            Assert.True(install.ExitCode == 0, $"install failed\nstdout:\n{install.Stdout}\n\nstderr:\n{install.Stderr}");
            Assert.True(File.Exists(Path.Combine(hermesHome, "skills", "mohist", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(hermesHome, "skills", "mohist", "references", "issue-templates.md")));
        }
        finally
        {
            if (Directory.Exists(publishRoot))
                Directory.Delete(publishRoot, recursive: true);
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeMainAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            var exitCode = await CliProgram.Main(args);
            return (exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeProcessAsync(string arguments)
    {
        var repoRoot = GetRepoRoot();
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --project packages/cli/Mohist.Cli -- {arguments}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static string GetRepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyList<(string Name, string? Value)> environment)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var (name, value) in environment)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }
}
