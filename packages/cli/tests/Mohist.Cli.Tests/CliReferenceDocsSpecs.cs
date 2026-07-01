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

        Assert.DoesNotContain("prerequisite-add", doc);
        Assert.DoesNotContain("prerequisite-remove", doc);
    }

    [Fact]
    public void CliReference_OptionNotesDoNotOverstateOutputOrProjectFlags()
    {
        var doc = ReadRepoText("docs/cli-reference.md");

        Assert.Contains("`list`、`show` 和 session 子命令支持 `-o table|json`", doc);
        Assert.Contains("`list` 支持 `-o table|json`；所有子命令支持 `--project`/`--project-id`", doc);
        Assert.Contains("顶层 `mo workflow` 不接受 `--project`/`--project-id`", doc);

        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo agent", doc);
        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo label", doc);
        Assert.DoesNotContain("所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。完整 flag 见 `mo workflow", doc);
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
