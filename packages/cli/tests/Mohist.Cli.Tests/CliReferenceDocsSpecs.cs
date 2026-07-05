using Xunit;

namespace Mohist.Cli.Tests;

public class CliReferenceDocsSpecs
{
    [Fact]
    public void CliReference_IssueCommandExamplesUseCurrentCommentAndPrereqSurface()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        Assert.Contains("mo issue comment add <number>", doc);
        Assert.Contains("mo issue prereq add <number> <prereq-number>", doc);
        Assert.Contains("mo issue prereq remove <number> <prereq-number>", doc);
        Assert.Contains("mo issue reject <number> --message <message>", doc);
        Assert.Contains("mo issue rerun-from-stage <number> --stage <stage>", doc);

        Assert.DoesNotContain("mo issue comment <number> <body>", doc);
        Assert.DoesNotContain("prerequisite-add", doc);
        Assert.DoesNotContain("prerequisite-remove", doc);
    }

    [Fact]
    public void IssueGuide_UsesCurrentPrereqCommandSurface()
    {
        var doc = ReadRepoText("docs/issues.md");

        Assert.Contains("mo issue prereq add 11 10", doc);
        Assert.Contains("mo issue prereq remove 11 10", doc);
        Assert.Contains("mo issue prereq add <number> <prereq-number>", doc);
        Assert.Contains("mo issue prereq remove <number> <prereq-number>", doc);
        Assert.Contains("mo issue reject <number> --message <message>", doc);
        Assert.Contains("mo issue rerun-from-stage <number> --stage <stage>", doc);

        Assert.DoesNotContain("prerequisite-add", doc);
        Assert.DoesNotContain("prerequisite-remove", doc);
        Assert.DoesNotContain("mo issue reject <number>\n", doc);
    }

    [Fact]
    public void CliReference_OptionNotesDoNotOverstateOutputOrProjectFlags()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        Assert.Contains("`list`、`show` 和 session 子命令支持 `-o table|json`", doc);
        Assert.Contains("`list` 支持 `-o table|json`；所有子命令支持 `--project`/`--project-id`", doc);
        Assert.Contains("项目作用域命令通常接受 `--project <name>` 和 `--project-id <id>`", doc);

        Assert.DoesNotContain("所有命令都接受 `--project <name>` 和 `--project-id <id>`", doc);
        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo agent", doc);
        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo label", doc);
        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo workflow", doc);
        Assert.DoesNotContain("顶层 `mo workflow list`", doc);
    }

    [Fact]
    public void CliReference_DocumentsRealTopLevelCommandGroupsAndCriticalSubcommands()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        string[] topLevelCommands =
        [
            "mo status",
            "mo logs",
            "mo info",
            "mo system info",
            "mo server start",
            "mo runner start",
            "mo install server",
            "mo update",
            "mo skills list",
            "mo project workflow profile list",
            "mo use <project>",
            "mo project create",
            "mo repo list",
            "mo issue create",
            "mo agent create",
            "mo epic create",
            "mo label list",
            "mo opencode models",
            "mo config get",
            "mo otel query"
        ];

        foreach (var command in topLevelCommands)
            Assert.Contains(command, doc);

        string[] criticalSubcommands =
        [
            "mo skills path <name>",
            "mo skills sync",
            "mo agent session launch",
            "mo label remove <key>",
            "mo otel status",
            "mo otel query <sql>"
        ];

        foreach (var command in criticalSubcommands)
            Assert.Contains(command, doc);

        Assert.Contains("工作树 skill-data 同步到托管缓存", doc);
    }

    [Fact]
    public void IssueGuide_UsesCurrentRejectAndStopSemantics()
    {
        var doc = ReadRepoText("docs/issues.md");

        Assert.Contains("mo issue reject 42 --message", doc);
        Assert.Contains("mo issue reject 42 -m", doc);
        Assert.Contains("`reject` 必须带理由", doc);
        Assert.DoesNotContain("reject 命令当前不带理由", doc);
        Assert.DoesNotContain("mo issue reject 42      # 打回", doc);

        Assert.Contains("永久停止（stop）", doc);
        Assert.Contains("terminal，不能 resume", doc);
        Assert.Contains("可恢复中断（force-stop）", doc);
        Assert.DoesNotContain("状态保留，可 resume", doc);
        Assert.DoesNotContain("软暂停（stop）", doc);
    }

    [Fact]
    public void EpicSkill_UsesCurrentPrereqCommandSurface()
    {
        var epic = ReadRepoText("packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md");

        Assert.Contains("mo issue prereq add <B-number> <A-number>", epic);
        Assert.Contains("mo issue prereq remove <B-number> <A-number>", epic);
        Assert.Contains("Use the API only as a fallback", epic);
        Assert.DoesNotContain("CLI does not yet have a prerequisite command", epic);
        Assert.DoesNotContain("curl -X POST", epic);
    }

    [Fact]
    public void SkillDocs_EpicDoneReadinessAllowsCancelledTerminalIssues()
    {
        var dispatcher = ReadRepoText("packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md");
        var epic = ReadRepoText("packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md");

        Assert.Contains("requires no open linked issues", dispatcher);
        Assert.Contains("cancelled issues satisfy readiness but do not count as delivered", dispatcher);
        Assert.DoesNotContain("requires all linked issues delivered", dispatcher);

        Assert.Contains("Requires **no open linked", epic);
        Assert.Contains("cancelled linked issues satisfy readiness but do not", epic);
        Assert.DoesNotContain("Requires **all** linked\n  issues delivered", epic);
        Assert.DoesNotContain("requires all linked issues delivered", epic);
    }

    private static string ReadRepoText(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
