using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("SkillsCli")]
public sealed class UpdateInstallSyncSpecs : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"mohist-update-install-sync-{Guid.NewGuid():N}");
    private readonly string? _originalOverrideEnv;

    public UpdateInstallSyncSpecs()
    {
        Directory.CreateDirectory(_tempRoot);
        _originalOverrideEnv = Environment.GetEnvironmentVariable(
            SkillAssetRootResolver.OverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(SkillAssetRootResolver.OverrideEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            SkillAssetRootResolver.OverrideEnvironmentVariable,
            _originalOverrideEnv);
        TryDeleteDirectory(_tempRoot);
    }

    [Fact]
    public async Task UpdateCliAsync_SynchronizesPublishedSkillData_IntoManagedCacheWithManifestAndBuiltInSkills()
    {
        var tempRoot = NewIsolatedRoot("update-basic");
        var publishSource = WritePackagedSkillAssets(tempRoot);

        var commands = new FakeCommandExecutor();
        var updater = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.True(File.Exists(Path.Combine(managedRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        var readManifest = SkillAssetManifest.TryRead(managedRoot);
        Assert.True(readManifest.IsFound);
        Assert.Contains("mohist", readManifest.Data!.Skills);
        Assert.Contains("mohist-explore", readManifest.Data.Skills);

        var sourceManifest = SkillAssetManifest.TryRead(publishSource);
        Assert.True(sourceManifest.IsFound);
        Assert.Equal(sourceManifest.Data!.Version, readManifest.Data.Version);
        Assert.Equal(sourceManifest.Data.GitHash, readManifest.Data.GitHash);
    }

    [Fact]
    public async Task UpdateCliAsync_ReplacesStaleManagedCacheContents_WithCurrentPublishOutput()
    {
        var tempRoot = NewIsolatedRoot("update-stale");
        WritePackagedSkillAssets(tempRoot);

        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Directory.CreateDirectory(Path.Combine(managedRoot, "mohist"));
        Directory.CreateDirectory(Path.Combine(managedRoot, "stale-skill"));
        await File.WriteAllTextAsync(
            Path.Combine(managedRoot, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: STALE\n---\n\n# STALE\n");
        await File.WriteAllTextAsync(
            Path.Combine(managedRoot, "stale-skill", "SKILL.md"),
            "STALE SKILL");
        await File.WriteAllTextAsync(Path.Combine(managedRoot, "stale.txt"), "stale-marker");
        SkillAssetManifest.Write(
            managedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "deadbeef"),
            new[] { "mohist", "mohist-explore", "stale-skill" });

        var commands = new FakeCommandExecutor();
        var updater = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(managedRoot, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(managedRoot, "stale-skill")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        var mohistBody = await File.ReadAllTextAsync(Path.Combine(managedRoot, "mohist", "SKILL.md"));
        Assert.DoesNotContain("STALE", mohistBody);

        var manifest = SkillAssetManifest.TryRead(managedRoot);
        Assert.True(manifest.IsFound);
        Assert.DoesNotContain("stale-skill", manifest.Data!.Skills);
        Assert.NotEqual("0.0.0-stale", manifest.Data.Version);
    }

    [Fact]
    public async Task UpdateCliAsync_DoesNotModifyExternalAgentSkillDirectories()
    {
        var tempRoot = NewIsolatedRoot("update-external-dirs");
        WritePackagedSkillAssets(tempRoot);

        var agentsSkillDir = Path.Combine(tempRoot, ".agents", "skills", "mohist-po");
        var claudeSkillDir = Path.Combine(tempRoot, ".claude", "skills", "user-skill");
        var hermesSkillDir = Path.Combine(tempRoot, ".hermes", "skills", "user-skill");
        var hermesConfig = Path.Combine(tempRoot, ".hermes", "config.yaml");
        Directory.CreateDirectory(agentsSkillDir);
        Directory.CreateDirectory(claudeSkillDir);
        Directory.CreateDirectory(hermesSkillDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsSkillDir, "SKILL.md"),
            "external-agent-skill");
        await File.WriteAllTextAsync(
            Path.Combine(claudeSkillDir, "SKILL.md"),
            "external-claude-skill");
        await File.WriteAllTextAsync(
            Path.Combine(hermesSkillDir, "SKILL.md"),
            "external-hermes-skill");
        await File.WriteAllTextAsync(hermesConfig, "skills:\n  external_dirs: []\n");

        var commands = new FakeCommandExecutor();
        var updater = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "external-agent-skill",
            await File.ReadAllTextAsync(Path.Combine(agentsSkillDir, "SKILL.md")));
        Assert.Equal(
            "external-claude-skill",
            await File.ReadAllTextAsync(Path.Combine(claudeSkillDir, "SKILL.md")));
        Assert.Equal(
            "external-hermes-skill",
            await File.ReadAllTextAsync(Path.Combine(hermesSkillDir, "SKILL.md")));
        Assert.Equal(
            "skills:\n  external_dirs: []\n",
            await File.ReadAllTextAsync(hermesConfig));
    }

    [Fact]
    public async Task UpdateCliAsync_DoesNotModifyRuntimeMohistSkillsDirectory()
    {
        var tempRoot = NewIsolatedRoot("update-runtime-mohist-skills");
        WritePackagedSkillAssets(tempRoot);

        var runtimeSkillsDir = Path.Combine(tempRoot, ".mohist", "skills");
        Directory.CreateDirectory(runtimeSkillsDir);
        var sentinelPath = Path.Combine(runtimeSkillsDir, "sentinel.txt");
        var nestedSkillPath = Path.Combine(runtimeSkillsDir, "internal-skill", "SKILL.md");
        await File.WriteAllTextAsync(sentinelPath, "runtime-sentinel");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedSkillPath)!);
        await File.WriteAllTextAsync(nestedSkillPath, "internal-skill-body");

        var before = SnapshotDirectory(runtimeSkillsDir);

        var commands = new FakeCommandExecutor();
        var updater = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, SnapshotDirectory(runtimeSkillsDir));
        Assert.Equal("runtime-sentinel", await File.ReadAllTextAsync(sentinelPath));
        Assert.Equal("internal-skill-body", await File.ReadAllTextAsync(nestedSkillPath));
    }

    [Fact]
    public async Task UpdateCliAsync_EnablesSkillAssetServiceResolution_WithoutMohistSkillsDirOverride()
    {
        var tempRoot = NewIsolatedRoot("update-resolution");
        WritePackagedSkillAssets(tempRoot);

        var commands = new FakeCommandExecutor();
        var updater = BuildUpdater(commands, tempRoot);

        var exitCode = await updater.UpdateCliAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo");
        Assert.Equal(0, exitCode);

        Assert.Null(Environment.GetEnvironmentVariable(SkillAssetRootResolver.OverrideEnvironmentVariable));

        var resolver = new SkillAssetRootResolver(
            getOverrideAssetRoot: () => null,
            getManagedAssetRoot: null,
            getUserHome: () => tempRoot,
            getBuildIdentity: SkillAssetManifest.ResolveCurrentBuildIdentity);
        var service = new SkillAssetService(resolver);

        Assert.Equal(SkillAssetRootSource.ManagedCache, service.AssetRootSource);
        var expectedManagedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.Equal(
            Path.GetFullPath(expectedManagedRoot),
            Path.GetFullPath(service.AssetRoot!));

        var mohistResult = service.GetSkill("mohist", includeSupplementaryFiles: false);
        Assert.True(mohistResult.Found, mohistResult.Error);
        Assert.Equal(
            Path.Combine(expectedManagedRoot, "mohist"),
            mohistResult.Skill!.DirectoryPath);

        var exploreResult = service.GetSkill("mohist-explore", includeSupplementaryFiles: false);
        Assert.True(exploreResult.Found, exploreResult.Error);
        Assert.Equal(
            Path.Combine(expectedManagedRoot, "mohist-explore"),
            exploreResult.Skill!.DirectoryPath);
    }

    [Fact]
    public async Task InstallScript_InstallsBinaryAndSynchronizesSkillData_WithoutTouchingExternalSkillDirectories()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        if (!IsCommandAvailable("dotnet") || !IsCommandAvailable("bash"))
            return;

        var repoRoot = GetRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "install-mo.sh");
        Assert.True(File.Exists(scriptPath), $"install-mo.sh missing at '{scriptPath}'");

        var tempHome = Path.Combine(_tempRoot, "install-script-home");
        Directory.CreateDirectory(tempHome);

        var staleManagedRoot = Path.Combine(tempHome, ".mohist", "cli", "skill-data");
        Directory.CreateDirectory(Path.Combine(staleManagedRoot, "stale-skill"));
        await File.WriteAllTextAsync(
            Path.Combine(staleManagedRoot, "stale-skill", "SKILL.md"),
            "stale-content-should-be-replaced");
        await File.WriteAllTextAsync(
            Path.Combine(staleManagedRoot, "stale.txt"),
            "stale-marker");
        SkillAssetManifest.Write(
            staleManagedRoot,
            new SkillAssetBuildIdentity("0.0.0-stale", "stale-hash"),
            new[] { "stale-skill" });

        var agentsSkillDir = Path.Combine(tempHome, ".agents", "skills", "mohist-po");
        var claudeSkillDir = Path.Combine(tempHome, ".claude", "skills", "user-skill");
        var hermesSkillDir = Path.Combine(tempHome, ".hermes", "skills", "user-skill");
        var runtimeSkillsDir = Path.Combine(tempHome, ".mohist", "skills");
        Directory.CreateDirectory(agentsSkillDir);
        Directory.CreateDirectory(claudeSkillDir);
        Directory.CreateDirectory(hermesSkillDir);
        Directory.CreateDirectory(runtimeSkillsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsSkillDir, "SKILL.md"),
            "external-agent-skill");
        await File.WriteAllTextAsync(
            Path.Combine(claudeSkillDir, "SKILL.md"),
            "external-claude-skill");
        await File.WriteAllTextAsync(
            Path.Combine(hermesSkillDir, "SKILL.md"),
            "external-hermes-skill");
        await File.WriteAllTextAsync(
            Path.Combine(runtimeSkillsDir, "sentinel.txt"),
            "runtime-sentinel");

        var externalDirsSnapshot = new[]
        {
            (Path.Combine(agentsSkillDir, "SKILL.md"), "external-agent-skill"),
            (Path.Combine(claudeSkillDir, "SKILL.md"), "external-claude-skill"),
            (Path.Combine(hermesSkillDir, "SKILL.md"), "external-hermes-skill"),
            (Path.Combine(runtimeSkillsDir, "sentinel.txt"), "runtime-sentinel"),
        };

        var install = await RunProcessAsync(
            "bash",
            $"\"{scriptPath}\"",
            repoRoot,
            new[]
            {
                ("HOME", tempHome),
                ("MOHIST_SKILLS_DIR", null),
            });

        Assert.True(
            install.ExitCode == 0,
            $"install-mo.sh failed (exit {install.ExitCode})\nstdout:\n{install.Stdout}\nstderr:\n{install.Stderr}");

        var binaryPath = Path.Combine(tempHome, ".local", "bin", "mo");
        Assert.True(File.Exists(binaryPath), $"Expected installed mo binary at '{binaryPath}'");

        var managedRoot = Path.Combine(tempHome, ".mohist", "cli", "skill-data");
        Assert.True(File.Exists(Path.Combine(managedRoot, "manifest.json")), "manifest.json should be present");
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")));

        Assert.False(
            File.Exists(Path.Combine(managedRoot, "stale.txt")),
            "Stale sentinel file should have been replaced.");
        Assert.False(
            Directory.Exists(Path.Combine(managedRoot, "stale-skill")),
            "Stale skill directory should have been replaced.");
        var manifest = SkillAssetManifest.TryRead(managedRoot);
        Assert.True(manifest.IsFound, manifest.Error);
        Assert.DoesNotContain("stale-skill", manifest.Data!.Skills);
        Assert.NotEqual("0.0.0-stale", manifest.Data.Version);

        foreach (var (path, expected) in externalDirsSnapshot)
        {
            Assert.True(File.Exists(path), $"Expected unchanged external file at '{path}'");
            Assert.Equal(expected, await File.ReadAllTextAsync(path));
        }
        Assert.False(
            Directory.Exists(Path.Combine(tempHome, ".mohist", "skills", "mohist")),
            "Runtime .mohist/skills must not receive packaged skill directories.");

        var getMohist = await RunProcessAsync(
            binaryPath,
            "skills get mohist",
            tempHome,
            new[]
            {
                ("HOME", tempHome),
                ("MOHIST_SKILLS_DIR", null),
            });
        Assert.True(
            getMohist.ExitCode == 0,
            $"mo skills get mohist failed\nstdout:\n{getMohist.Stdout}\nstderr:\n{getMohist.Stderr}");
        Assert.Contains("name: mohist", getMohist.Stdout, StringComparison.Ordinal);

        var pathMohist = await RunProcessAsync(
            binaryPath,
            "skills path mohist",
            tempHome,
            new[]
            {
                ("HOME", tempHome),
                ("MOHIST_SKILLS_DIR", null),
            });
        Assert.True(
            pathMohist.ExitCode == 0,
            $"mo skills path mohist failed\nstdout:\n{pathMohist.Stdout}\nstderr:\n{pathMohist.Stderr}");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(managedRoot, "mohist")),
            Path.GetFullPath(pathMohist.Stdout.Trim()));
    }

    private string NewIsolatedRoot(string label)
    {
        var root = Path.Combine(_tempRoot, label);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WritePackagedSkillAssets(string tempRoot)
    {
        var publishSource = Path.Combine(tempRoot, ".publish", "cli", "skill-data");
        Directory.CreateDirectory(Path.Combine(publishSource, "mohist"));
        Directory.CreateDirectory(Path.Combine(publishSource, "mohist-explore"));
        File.WriteAllText(
            Path.Combine(publishSource, "mohist", "SKILL.md"),
            BuildSkillMarkdown("mohist"));
        File.WriteAllText(
            Path.Combine(publishSource, "mohist-explore", "SKILL.md"),
            BuildSkillMarkdown("mohist-explore"));
        var identity = SkillAssetManifest.ResolveCurrentBuildIdentity();
        SkillAssetManifest.Write(publishSource, identity, new[] { "mohist", "mohist-explore" });
        return publishSource;
    }

    private static string BuildSkillMarkdown(string name) =>
        $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {name}\n";

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static SourceCodeUpdater BuildUpdater(FakeCommandExecutor commands, string tempRoot)
    {
        var files = new InMemoryFileSystem();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        return new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            new HttpClient(new ConstantStatusHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
    }

    private static IReadOnlyList<string> SnapshotDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();
        return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCommandAvailable(string commandName)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("sh", $"-lc \"command -v {commandName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task WriteAllTextAsync(string path, string contents)
        {
            _files[Path.GetFullPath(path)] = contents;
            return Task.CompletedTask;
        }

        public Task<string> ReadAllTextAsync(string path) => Task.FromResult(_files[Path.GetFullPath(path)]);

        public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => false;

        public void CreateDirectory(string path)
        {
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => [];

        public void Delete(string path) => _files.Remove(Path.GetFullPath(path));
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }

    private sealed class ConstantStatusHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public ConstantStatusHttpHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("<html></html>"),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }
}
