using Xunit;

namespace Mohist.Server.UnitTests.Epic.Docs;

public class EpicDocumentationTests
{
    [Fact]
    public void EpicDocs_DescribeTheSelfDrivingLifecycleContract()
    {
        var epics = ReadDoc("epics.md");

        Assert.Contains("| `idle` |", epics);
        Assert.Contains("| `running` |", epics);
        Assert.Contains("| `paused` |", epics);
        Assert.Contains("| `done` |", epics);
        Assert.Contains("| `closed` |", epics);
        Assert.Contains("新建 Epic 默认为 `idle`", epics);
        Assert.Contains("`done` 和 `closed` 是终态", epics);
        Assert.DoesNotContain("active / done / closed", epics);
        Assert.DoesNotContain("Epic 只是组织工具，不参与执行", epics);

        Assert.Contains("mo epic start <id>", epics);
        Assert.Contains("mo epic pause <id>", epics);
        Assert.Contains("mo epic resume <id>", epics);
        Assert.Contains("**Start Epic**", epics);
        Assert.Contains("**Pause**", epics);
        Assert.Contains("**Resume**", epics);
        Assert.Contains("/api/projects/{project}/epics/{id}/start", epics);
        Assert.Contains("/api/projects/{project}/epics/{id}/pause", epics);
        Assert.Contains("/api/projects/{project}/epics/{id}/resume", epics);
        Assert.Contains("尝试推进第一个 startable linked issue", epics);
        Assert.Contains("**Idempotency**", epics);
    }

    [Fact]
    public void EpicDocs_DescribeAutoAdvancementRunningButIdleAndAutoDoneAccurately()
    {
        var epics = ReadDoc("epics.md");

        Assert.Contains("`running` 的 Epic 会在当前 in-progress linked issue 到达终态", epics);
        Assert.Contains("`idle` 和 `paused` 状态的 Epic **不会自动推进**", epics);
        Assert.Contains("仍有 open linked issue、但没有可推进的 next startable issue", epics);
        Assert.Contains("`progress.nextIssueReason` 字段会解释当前为什么没有推进", epics);
        Assert.Contains("不是第六个状态", epics);
        Assert.Contains("没有 linked issues 时，详情页会显示 empty-epic 信息", epics);
        Assert.Contains("`readyToMarkDone` 会变为 true", epics);
        Assert.Contains("自动转为 `done`", epics);
        Assert.DoesNotContain("例如所有 linked issue 已完成", epics);
    }

    [Fact]
    public void SurfaceDocs_StayAlignedWithEpicCliAndWebUiCopy()
    {
        var webUi = ReadDoc("web-ui.md");
        var cliReference = ReadDoc("cli-reference.md");

        Assert.Contains("`idle` / `running` Epic 的当前工作进度分组", webUi);
        Assert.Contains("**Running** | `idle` / `running` Epic 中有 linked issue 正在 in-progress", webUi);
        Assert.Contains("不等同于所有卡片都是 `running` 生命周期状态", webUi);
        Assert.Contains("**Paused** | 暂停推进（当前 in-progress issue 不中断） | 有 paused Epic 时显示", webUi);
        Assert.Contains("**Start Epic**", webUi);
        Assert.Contains("**Pause**", webUi);
        Assert.Contains("**Resume**", webUi);
        Assert.Contains("**Mark Done**", webUi);

        Assert.Contains("mo epic create <title> [options]", cliReference);
        Assert.Contains("mo epic start <epic-id-or-number>", cliReference);
        Assert.Contains("mo epic pause <epic-id-or-number>", cliReference);
        Assert.Contains("mo epic resume <epic-id-or-number>", cliReference);
        Assert.DoesNotContain("当前 CLI 不支持的：Epic 管理", cliReference);
        Assert.DoesNotContain("Epic 管理（用 API", cliReference);
    }

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

    [Fact]
    public void EpicDocs_DoNotNameClosedAsALinkedIssueTerminalStatus()
    {
        var epics = ReadDoc("epics.md");

        Assert.Contains("终态（`done` / `cancelled`）", epics);
        Assert.DoesNotContain("终态（`done` / `closed` / `cancelled`）", epics);
    }

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
