using Xunit;

namespace Mohist.Cli.SpecTests;

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
        Assert.Contains("mo issue rerun <number> --from-stage <stage>", doc);

        Assert.DoesNotContain("mo issue comment <number> <body>", doc);
        Assert.DoesNotContain("prerequisite-add", doc);
        Assert.DoesNotContain("prerequisite-remove", doc);
    }

    [Fact]
    public void CliReference_DocumentsWorkflowProfileToggleProfileIdArgument()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        Assert.Contains("mo project workflow profile enable <profile-id>", doc);
        Assert.Contains("mo project workflow profile disable <profile-id>", doc);
        Assert.DoesNotContain("mo project workflow profile enable                  启用 profile", doc);
        Assert.DoesNotContain("mo project workflow profile disable                 禁用 profile", doc);
    }

    [Fact]
    public void CliReference_DocumentsCanonicalIssueRerunFromStageFlag()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        Assert.Contains("mo issue rerun <编号> --from-stage <阶段>", doc);
        Assert.DoesNotContain("mo issue rerun-from-stage <编号> --stage <阶段>", doc);
        Assert.DoesNotContain("mo issue rerun-from-stage --stage", doc);
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
        Assert.Contains("mo issue rerun <number> --from-stage <stage>", doc);

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

        // T-006 of issue #387: the five legacy bare-verb / misnamed paths
        // (`mo status`, `mo logs`, `mo use <project>`, `mo notify setup`,
        // `mo system info`) are migrated to canonical resource-group paths.
        // The legacy strings must NOT appear in the doc; the canonical ones
        // must. We anchor the negative assertions on common surface forms
        // (newline-terminated in code blocks, backtick-wrapped in prose,
        // or as a gap-table row) to avoid false positives from substring
        // matches inside other text.
        //
        // T-001 of issue #388 extends the legacy guard with the install/
        // update double-entry paths and the migration note that tracked
        // that convergence. After the convergence lands the doc must not
        // re-advertise any of these (the gap table is also gone — see
        // `CliReference_DoesNotAdvertiseInstallUpdateDoubleEntry_GapTable`).
        string[] forbiddenLegacyPathRows =
        [
            "mo status",
            "mo logs",
            "mo use <project>",
            "mo notify setup",
            "mo system info",
            "mo server install",
            "mo server update",
            "mo runner install",
        ];
        // None of the legacy paths may appear in the doc at all (this
        // document is the authoritative command surface).
        foreach (var legacy in forbiddenLegacyPathRows)
            Assert.DoesNotContain(legacy, doc);

        // The convergence closes the gap the migration note tracked;
        // the note must be gone with the gap.
        Assert.DoesNotContain("命令路径迁移", doc);

        string[] topLevelCommands =
        [
            "mo info",
            "mo project status",
            "mo system logs",
            "mo project use",
            "mo notification setup",
            "mo server info",
            "mo server start",
            "mo runner start",
            "mo install server",
            "mo update",
            "mo skills list",
            "mo project workflow profile list",
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
            "mo label delete <key>",
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
        Assert.Contains("可恢复暂停（force-stop）", doc);
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
