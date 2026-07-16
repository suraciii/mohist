# 快速上手

目标：30 分钟内从零启动 Mohist，跑通一个真实的 issue，看到代码被合并。

## 前置条件

| 工具 | 版本 | 检查命令 |
|---|---|---|
| .NET SDK | 11.0+ | `dotnet --version` |
| Node.js | 22+ | `node --version` |
| npm | 10+ | `npm --version` |
| opencode CLI | 可正常启动 | `opencode --version` |

如果 `opencode` 没装，按 [opencode 官方文档](https://opencode.ai) 装。Mohist 不内置 AI 模型，Inline Agent 依赖 OpenCode 执行任务。

## 1. 获取代码 + 安装依赖

```bash
git clone <your-fork-or-mohist-url> mohist
cd mohist
npm install
```

`npm install` 会装好 Web UI 和 Runner 的所有依赖。

## 2. 构建后端

```bash
npm run build
```

这会编译 ASP.NET Core server 和 CLI。第一次会慢一点（要还原 NuGet 包）。

## 3. 启动三个进程

Mohist 由三个进程组成。开三个终端分别跑：

```bash
# 终端 1：控制平面
npm run dev:server

# 终端 2：执行平面（必须在 server 之后）
npm run dev:runner

# 终端 3：Web UI（开发服务器）
npm run dev:web
```

打开 `http://localhost:3456`，看到看板就说明起来了。

> 生产或长跑场景请参考 [Self-host 部署](self-host.md)，不用三个终端。

## 4. 配置 Inline Agent 模型

Mohist 通过 opencode 调用 LLM。你需要确保 opencode 能正常工作：

```bash
# 测试 opencode 是否能调通
opencode --help
```

不指定模型时，Inline Agent 使用 OpenCode 的默认模型。需要显式选择时，可以
直接写在 task 的 `options`，也可以在 Workflow variables 中配置后通过
`options: ${{ vars.agent }}` 传入。完整配置见
[`mohist/opencode` Action](actions/opencode.md)。

## 5. 创建你的第一个项目

Web UI 上：

1. 点 **Create Project**
2. 填项目名（如 `my-app`）
3. 填初始仓库的资源名（如 `server`）和 Git URL
4. 确认 base branch（默认 `main`）

或用 CLI：

```bash
mo project create my-app --path /path/to/your/repo
mo project use my-app
```

## 6. 创建第一个 Issue

写一个简单、清晰、可验证的 issue 作为试验。比如：

> Title: Add hello world endpoint
>
> Body: Add a `GET /hello` endpoint that returns `{ "message": "hello" }`.

CLI：

```bash
mo issue create "Add hello world endpoint" \
  --body "Add a GET /hello endpoint that returns {\"message\":\"hello\"}."
```

或 Web UI 看板右上角 **New Issue**。

## 7. 启动 Issue

```bash
mo issue start 1
```

或在 Web UI 看板上点 issue 进入详情页 → **Start**。

这时 Mohist 会：
1. 创建 worktree（`mo/issue-1` 分支）
2. 进入 **Plan** 阶段，Inline Agent 开始分析需求、产出 proposal/design/specs/tasks

## 8. 等待 Plan 完成

Plan 阶段通常 5-20 分钟（取决于 issue 复杂度和模型速度）。你可以在：

- Web UI issue 详情页看实时进度
- `mo issue logs 1` 看详细日志
- `mo issue show 1` 看当前状态

Plan 完成后，issue 会停在 **awaiting approval** 状态，表示 workflow 正在等待审批决策。

## 9. 审批 Plan

进入 issue 详情页，看最新产物面板里的产物：

- `proposal.md` — Inline Agent 对这个需求的理解
- `design.md` — 设计决策
- `specs/` — 规格变更
- `tasks.json` — 接下来 Build 阶段会执行的步骤
- `self-review.md` — Inline Agent 自己的 review

读一遍，觉得合理就点 **Approve**。觉得有问题就 **Reject**（Inline Agent 会重新 plan）。这一步处理的是 workflow 的审批点；动作可以来自 Web UI、CLI、Mohist Agent 或其它自动化。

```bash
mo issue approve 1     # 批准
mo issue reject 1      # 打回
```

## 10. 观察 Build / Check / Integrate

审批通过后 workflow 自动推进：

- **Build**：Inline Agent 按 tasks.json 写代码、跑测试
- **Check**：Inline Agent review 自己的产出，可能再次等待审批
- **Integrate**：把 `mo/issue-1` 分支合并回 base branch

任何阶段失败，issue 会进入 blocked 状态。看 [故障恢复](troubleshooting.md) 怎么处理。

## 11. 验收 Issue

Integrate 完成后，issue 进入 Done。这时：

- `mo/issue-1` 分支已经合并进你的 base branch
- 你的仓库里有了实际的代码改动
- 所有产物留在 `openspec/changes/1-<slug>/` 下作为审计记录

去你的仓库验证一下 `GET /hello` 真的工作。

## 下一步

- [核心概念](concepts.md) — 理解你刚才用到的所有名词
- [Issue 管理](issues.md) — 学会 prerequisites、comments、force stop、retry 等
- [用 Epic 规划](epics.md) — 把零散 issue 组织成可自动推进的产品路线
- [Workflow Profile](workflow-profiles.md) — 改造 workflow 适配你的工作风格
- [CLI 参考](cli-reference.md) — `mo` 完整命令、选项、退出码

---

对应源码：仓库根 `package.json`、`global.json`、`Directory.Build.props`。
