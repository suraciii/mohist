using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Docs;

public class EpicDocumentationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void EpicDocs_DescribeMarkDoneReadinessWithoutEquatingItToDeliveredCount()
    {
        var epics = ReadDoc("epics.md");
        var webUi = ReadDoc("web-ui.md");

        Assert.Contains("没有 open linked issues", epics);
        Assert.Contains("cancelled issue 是终态", epics);
        Assert.Contains("`deliveredCount` 仍只统计已 delivered 的 issue", epics);
        Assert.DoesNotContain("所有 issue delivered", epics);
        Assert.DoesNotContain("所有 issue 必须已经 delivered", epics);

        Assert.Contains("`readyToMarkDone` 为 true（所有 linked issues 都已进入终态，没有 open linked issues）", webUi);
        Assert.Contains("delivered 只统计已完成交付的 issue", webUi);
        Assert.DoesNotContain("所有 linked issues 已 delivered（readyToMarkDone）", webUi);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void EpicDocs_DoNotNameClosedAsALinkedIssueTerminalStatus()
    {
        var epics = ReadDoc("epics.md");

        Assert.Contains("终态（`done` / `cancelled`）", epics);
        Assert.DoesNotContain("终态（`done` / `closed` / `cancelled`）", epics);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void EditedDocs_DoNotRegressKnownMarkdownFormattingIssues()
    {
        var concepts = ReadDoc("concepts.md");
        var cliReference = ReadDoc("cli-reference.md");

        Assert.DoesNotContain("详见[", concepts);
        Assert.DoesNotContain("\n\n\n## 退出码", cliReference);
    }

    private static string ReadDoc(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../docs", fileName));
        return File.ReadAllText(path);
    }
}
