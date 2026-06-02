using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class SkillsCommandBehaviorSpecs
{
    [Fact]
    public async Task PublishedCommands_ResolveFromManagedCache_WhenManagedCacheIsPresent()
    {
        var repoRoot = GetRepoRoot();
        var publishRoot = Path.Combine(Path.GetTempPath(), $"mohist-cli-behavior-publish-{Guid.NewGuid():N}");
        var tempHome = Path.Combine(Path.GetTempPath(), $"mohist-cli-behavior-home-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(Path.GetTempPath(), $"mohist-cli-behavior-install-{Guid.NewGuid():N}");
        var hermesHome = Path.Combine(tempHome, "hermes");
        Directory.CreateDirectory(publishRoot);
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(installRoot);

        try
        {
            var publish = await RunProcessAsync(
                "dotnet",
                "publish packages/cli/Mohist.Cli -c Release -o \"" + publishRoot + "\"",
                repoRoot,
                []);

            Assert.True(
                publish.ExitCode == 0,
                $"publish failed\nstdout:\n{publish.Stdout}\n\nstderr:\n{publish.Stderr}");

            var managedRoot = StageManagedCacheAsync(publishRoot, tempHome);
            var cliDll = Path.Combine(publishRoot, "Mohist.Cli.dll");

            var list = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills list",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(list.ExitCode == 0, $"list failed\nstdout:\n{list.Stdout}\n\nstderr:\n{list.Stderr}");
            var listLines = SplitLines(list.Stdout);
            Assert.Equal(2, listLines.Length);
            Assert.StartsWith("mohist\t", listLines[0], StringComparison.Ordinal);
            Assert.StartsWith("mohist-explore\t", listLines[1], StringComparison.Ordinal);

            var listJson = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills list --json",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(listJson.ExitCode == 0, $"list --json failed\nstderr:\n{listJson.Stderr}");
            var listedSkills = System.Text.Json.JsonSerializer.Deserialize<List<SkillListItem>>(listJson.Stdout);
            Assert.NotNull(listedSkills);
            Assert.Equal(new[] { "mohist", "mohist-explore" }, listedSkills!.Select(skill => skill.Name).ToArray());

            var getMohist = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills get mohist",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(getMohist.ExitCode == 0, $"get mohist failed\nstderr:\n{getMohist.Stderr}");
            Assert.Contains("name: mohist", getMohist.Stdout);
            Assert.Contains("Use this skill for current Mohist .NET backend", getMohist.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("mo skills get mohist --full", getMohist.Stdout, StringComparison.Ordinal);

            var getExplore = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills get mohist-explore",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(getExplore.ExitCode == 0, $"get mohist-explore failed\nstderr:\n{getExplore.Stderr}");
            Assert.Contains("name: mohist-explore", getExplore.Stdout);
            Assert.Contains("Use this skill to explore Mohist from the product and user perspective", getExplore.Stdout, StringComparison.Ordinal);

            var getFull = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills get mohist --full",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(getFull.ExitCode == 0, $"get mohist --full failed\nstderr:\n{getFull.Stderr}");
            var fullMarker = "--- references/issue-templates.md ---";
            Assert.Contains(fullMarker, getFull.Stdout, StringComparison.Ordinal);
            Assert.True(getFull.Stdout.IndexOf(fullMarker, StringComparison.Ordinal) > getFull.Stdout.IndexOf("name: mohist", StringComparison.Ordinal));

            var getAll = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills get --all",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(getAll.ExitCode == 0, $"get --all failed\nstderr:\n{getAll.Stderr}");
            var mohistIndex = getAll.Stdout.IndexOf("## mohist", StringComparison.Ordinal);
            var exploreIndex = getAll.Stdout.IndexOf("## mohist-explore", StringComparison.Ordinal);
            Assert.True(mohistIndex >= 0);
            Assert.True(exploreIndex > mohistIndex);

            var pathOutput = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills path mohist",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(pathOutput.ExitCode == 0, $"skills path failed\nstderr:\n{pathOutput.Stderr}");
            var expectedPath = Path.Combine(managedRoot, "mohist");
            Assert.Equal(expectedPath, pathOutput.Stdout.Trim());

            var pathJson = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills path mohist --json",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(pathJson.ExitCode == 0, $"skills path --json failed\nstderr:\n{pathJson.Stderr}");
            using var pathDoc = System.Text.Json.JsonDocument.Parse(pathJson.Stdout);
            Assert.Equal("mohist", pathDoc.RootElement.GetProperty("name").GetString());
            Assert.Equal(expectedPath, pathDoc.RootElement.GetProperty("path").GetString());

            var installRepoRoot = Path.Combine(installRoot, "repo");
            Directory.CreateDirectory(installRepoRoot);
            var installRepo = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills install",
                installRepoRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(installRepo.ExitCode == 0, $"skills install failed\nstdout:\n{installRepo.Stdout}\n\nstderr:\n{installRepo.Stderr}");
            AssertDiscoveryStubOnly(Path.Combine(installRepoRoot, ".agents", "skills", "mohist", "SKILL.md"), "mohist");
            AssertDiscoveryStubOnly(Path.Combine(installRepoRoot, ".agents", "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
            Assert.False(Directory.Exists(Path.Combine(installRepoRoot, ".claude", "skills")), "Repository install must not write to .claude.");
            Assert.False(Directory.Exists(Path.Combine(hermesHome, "skills")), "Repository install must not write to Hermes.");

            var installClaudeRoot = Path.Combine(installRoot, "claude");
            Directory.CreateDirectory(installClaudeRoot);
            var installClaude = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills install --claude",
                installClaudeRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(installClaude.ExitCode == 0, $"skills install --claude failed\nstderr:\n{installClaude.Stderr}");
            AssertDiscoveryStubOnly(Path.Combine(installClaudeRoot, ".claude", "skills", "mohist", "SKILL.md"), "mohist");
            AssertDiscoveryStubOnly(Path.Combine(installClaudeRoot, ".claude", "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
            Assert.False(Directory.Exists(Path.Combine(installClaudeRoot, ".agents", "skills")), "Claude install must not write to .agents.");
            Assert.False(Directory.Exists(Path.Combine(hermesHome, "skills")), "Claude install must not write to Hermes.");

            var installHermes = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills install --hermes",
                installRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(installHermes.ExitCode == 0, $"skills install --hermes failed\nstdout:\n{installHermes.Stdout}\n\nstderr:\n{installHermes.Stderr}");
            AssertFullPackagedHermesSkill(Path.Combine(hermesHome, "skills", "mohist", "SKILL.md"), "mohist");
            AssertFullPackagedHermesSkill(Path.Combine(hermesHome, "skills", "mohist-explore", "SKILL.md"), "mohist-explore");
            Assert.True(File.Exists(Path.Combine(hermesHome, "skills", "mohist", "references", "issue-templates.md")));
            Assert.Contains("/mohist", installHermes.Stdout + installHermes.Stderr, StringComparison.Ordinal);
            Assert.Contains("/mohist-explore", installHermes.Stdout + installHermes.Stderr, StringComparison.Ordinal);
            Assert.Contains("reload/reset", installHermes.Stdout + installHermes.Stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(installRoot, ".agents", "skills")), "Hermes install must not write to .agents.");
            Assert.False(Directory.Exists(Path.Combine(installRoot, ".claude", "skills")), "Hermes install must not write to .claude.");

            var userSkillPath = Path.Combine(installRepoRoot, ".agents", "skills", "mohist-po", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(userSkillPath)!);
            await File.WriteAllTextAsync(userSkillPath, "user-authored");
            var sentinelPath = Path.Combine(tempHome, ".mohist", "skills", "sentinel.txt");
            await EnsureRuntimeSentinelAsync(sentinelPath);

            var sentinelBefore = await File.ReadAllTextAsync(sentinelPath);
            var userSkillBefore = await File.ReadAllTextAsync(userSkillPath);

            var secondInstallRepo = await RunProcessAsync(
                "dotnet",
                "\"" + cliDll + "\" skills install",
                installRepoRoot,
                BuildHomeEnvironment(tempHome, hermesHome));
            Assert.True(secondInstallRepo.ExitCode == 0, $"repeat install failed\nstderr:\n{secondInstallRepo.Stderr}");

            Assert.Equal(userSkillBefore, await File.ReadAllTextAsync(userSkillPath));
            Assert.Equal(sentinelBefore, await File.ReadAllTextAsync(sentinelPath));
            Assert.False(Directory.Exists(Path.Combine(tempHome, ".mohist", "skills", "mohist")), "Runtime .mohist/skills must not receive a Mohist skill directory.");
        }
        finally
        {
            if (Directory.Exists(publishRoot))
                Directory.Delete(publishRoot, recursive: true);
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
            if (Directory.Exists(installRoot))
                Directory.Delete(installRoot, recursive: true);
        }
    }

    private static string StageManagedCacheAsync(string publishRoot, string tempHome)
    {
        var sourceSkillData = Path.Combine(publishRoot, "skill-data");
        Assert.True(Directory.Exists(sourceSkillData), $"Published skill-data missing at '{sourceSkillData}'.");
        Assert.True(File.Exists(Path.Combine(sourceSkillData, "manifest.json")), "Published manifest.json missing.");

        var managedRoot = Path.Combine(tempHome, ".mohist", "cli", "skill-data");
        CopyDirectory(sourceSkillData, managedRoot);

        Assert.True(File.Exists(Path.Combine(managedRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        return managedRoot;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destDir, relative);
            var destinationSubdir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationSubdir))
                Directory.CreateDirectory(destinationSubdir);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static IReadOnlyList<(string Name, string? Value)> BuildHomeEnvironment(string tempHome, string hermesHome)
    {
        return new (string, string?)[]
        {
            ("MOHIST_SKILLS_DIR", null),
            ("HOME", tempHome),
            ("USERPROFILE", tempHome),
            ("HERMES_HOME", hermesHome),
            ("DOTNET_CLI_WORKING_DIR", null),
        };
    }

    private static async Task EnsureRuntimeSentinelAsync(string sentinelPath)
    {
        var dir = Path.GetDirectoryName(sentinelPath)!;
        Directory.CreateDirectory(dir);
        if (!File.Exists(sentinelPath))
            await File.WriteAllTextAsync(sentinelPath, "runtime-sentinel");
    }

    private static void AssertDiscoveryStubOnly(string path, string name)
    {
        Assert.True(File.Exists(path), $"Expected discovery stub at '{path}'.");
        var content = File.ReadAllText(path);
        Assert.Contains("---", content);
        Assert.Contains($"name: {name}", content);
        Assert.Contains($"mo skills get {name}", content);
        Assert.Contains("This Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Use this skill for current Mohist .NET backend", content, StringComparison.Ordinal);
        Assert.DoesNotContain("references/issue-templates.md", content, StringComparison.Ordinal);
        Assert.DoesNotContain("templates/", content, StringComparison.Ordinal);
    }

    private static void AssertFullPackagedHermesSkill(string path, string name)
    {
        Assert.True(File.Exists(path), $"Expected full Hermes skill at '{path}'.");
        var content = File.ReadAllText(path);
        Assert.Contains($"name: {name}", content);
        Assert.Contains("---", content);
        Assert.DoesNotContain("This Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.", content, StringComparison.Ordinal);
        if (name == "mohist")
            Assert.Contains("Use this skill for current Mohist .NET backend", content, StringComparison.Ordinal);
        else
            Assert.Contains("Use this skill to explore Mohist from the product and user perspective", content, StringComparison.Ordinal);
    }

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string GetRepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));

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

    private sealed class SkillListItem
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
    }
}
