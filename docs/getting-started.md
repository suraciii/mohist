# 快速上手

目标：30 分钟内从零启动 Mohist，通过外部 Agent 或 `mo` 跑通一个真实的 Issue，看到
代码被合并。Web UI 作为可选的观察与人工操作平面。

## 前置条件

| 工具 | 版本 | 检查命令 |
|---|---|---|
| .NET SDK | 11.0+ | `dotnet --version` |
| Node.js | 22.19.0+ | `node --version` |
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

## 3. 启动核心进程

Server 和 Runner 是 Mohist 执行所需的核心进程。开两个终端分别跑：

```bash
# 终端 1：控制平面
npm run dev:server

# 终端 2：执行平面（必须在 server 之后）
npm run dev:runner
```

需要备用操作和可视化平面时，再启动 Web UI：

```bash
# 可选终端 3：Web UI（开发服务器）
npm run dev:web
```

打开 `http://localhost:3456` 可以查看看板；外部 Agent 和 `mo` 不依赖 Web UI 运行。

> 生产或长跑场景请参考 [Self-host 部署](self-host.md)，无需分别启动这些开发进程。

## 4. 让外部 Agent 认识 Mohist

把 Mohist Skill 安装到本机支持的外部 Agent：

```bash
mo skill install
```

之后可以直接在外部 Agent 所在的 Slack、IDE 或其他交互场所提出需求，例如“查看 Mohist
当前有哪些 Issue 在推进，是否需要我处理”。外部 Agent 会按场景读取 Skill，并通过 `mo`
查询或操作 Mohist。具体机制见 [Skill 机制](skills.md)。

不使用外部 Agent 时，可以直接执行本文中的 `mo` 命令。

## 5. 配置 Inline Agent 模型

Mohist 通过 opencode 调用 LLM。你需要确保 opencode 能正常工作：

```bash
# 测试 opencode 是否能调通
opencode --help
```

不指定模型时，Inline Agent 使用 OpenCode 的默认模型。需要显式选择时，可以
直接写在 task 的 `options`，也可以在 Workflow variables 中配置后通过
`options: ${{ vars.agent }}` 传入。完整配置见
[`mohist/opencode` Action](actions/opencode.md)。

## 6. 创建你的第一个项目

用 CLI 创建 Project：

```bash
mo project create my-app --path /path/to/your/repo
mo project use my-app
```

也可以让已经安装 Mohist Skill 的外部 Agent 执行同一操作。需要人工备用入口时，在 Web UI：

1. 点 **Create Project**
2. 填项目名（如 `my-app`）
3. 填初始仓库的资源名（如 `server`）和 Git URL
4. 确认 base branch（默认 `main`）

## 7. 创建第一个 Issue

写一个简单、清晰、可验证的 issue 作为试验。比如：

> Title: Add hello world endpoint
>
> Body: Add a `GET /hello` endpoint that returns `{ "message": "hello" }`.

在外部 Agent 中可以直接说：

```text
在 my-app 创建一个 ready Issue：增加 GET /hello，返回 { "message": "hello" }。
```

外部 Agent 会整理需求并使用 `mo` 创建。直接使用 CLI 时：

```bash
mo issue create "Add hello world endpoint" \
  --body "Add a GET /hello endpoint that returns {\"message\":\"hello\"}."
```

备用路径是 Web UI 看板右上角 **New Issue**。

## 8. 启动 Issue

可以让外部 Agent“启动刚创建的 Issue”，也可以直接执行：

```bash
mo issue start 1
```

备用路径是在 Web UI 看板上点 Issue 进入详情页 → **Start**。

这时 Mohist 会：
1. 创建 worktree（`mo/issue-1` 分支）
2. 进入 **Plan** 阶段，Inline Agent 开始分析需求、产出 proposal/design/specs/tasks

## 9. 等待 Plan 完成

Plan 阶段通常 5-20 分钟（取决于 issue 复杂度和模型速度）。你可以在：

- 外部 Agent 中询问“#1 推进到哪里，是否有问题”
- `mo issue logs 1` 看详细日志
- `mo issue view 1` 看当前状态
- Web UI Issue 详情页查看完整进度和执行证据

Plan 完成后，issue 会停在 **awaiting approval** 状态，表示 workflow 正在等待审批决策。

## 10. 审批 Plan

让外部 Agent 汇总 Plan 产物、风险和建议，或者进入 Web UI Issue 详情页查看最新产物：

- `proposal.md` — Inline Agent 对这个需求的理解
- `design.md` — 设计决策
- `specs/` — 规格变更
- `tasks.json` — 接下来 Build 阶段会执行的步骤
- `self-review.md` — Inline Agent 自己的 review

觉得合理就批准，觉得有问题就附带理由打回（Inline Agent 会重新 plan）。这一步处理的是
Workflow 的审批点；动作可以来自外部 Agent、Web UI、CLI、Mohist Agent 或其它自动化。
外部 Agent 和自动化执行时应署名；人直接操作时可以不署名。

```bash
mo run approve --issue 1                         # 批准
mo run reject --issue 1 --message "需要修改的内容"  # 打回
```

## 11. 观察 Build / Check / Integrate

审批通过后 workflow 自动推进：

- **Build**：Inline Agent 按 tasks.json 写代码、跑测试
- **Check**：Inline Agent review 自己的产出，可能再次等待审批
- **Integrate**：把 `mo/issue-1` 分支合并回 base branch

任何阶段失败，issue 会进入 blocked 状态。看 [故障恢复](troubleshooting.md) 怎么处理。

## 12. 验收 Issue

Integrate 完成后，issue 进入 Done。这时：

- `mo/issue-1` 分支已经合并进你的 base branch
- 你的仓库里有了实际的代码改动
- 所有产物留在 `openspec/changes/1-<slug>/` 下作为审计记录

去你的仓库验证一下 `GET /hello` 真的工作。

## 下一步

- [Skill 机制](skills.md) — 让外部 Agent 查询、委托和操作 Mohist
- [核心概念](concepts.md) — 理解你刚才用到的所有名词
- [Issue 管理](issues.md) — 学会 prerequisites、comments、force stop、retry 等
- [用 Epic 规划](epics.md) — 把零散 issue 组织成可自动推进的产品路线
- [Workflow Profile](workflow-profiles.md) — 改造 workflow 适配你的工作风格
- [CLI 参考](cli-reference.md) — `mo` 完整命令、选项、退出码

---

对应源码：仓库根 `package.json`、`global.json`、`Directory.Build.props`。
