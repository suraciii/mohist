# 分支拓扑：多 PR 重叠时的默认策略

多个 PR 并行修改同一批文件时，开枝、排序、合并的默认策略：开枝前画文件重叠图，
有重叠/依赖的 PR 用 stack 或单一集成分支，spec 先入主干。

只覆盖多 PR 重叠/依赖场景；单 PR 独立交付不适用。不强制所有场景，也不约束
Mohist Workflow 内部的 task dispatch（那是 Workflow Definition 的职责）。

## 背景：strict + linear 下的合并摩擦

master 的分支保护是两道硬约束，合起来决定了"落后 master 的 PR 只能 rebase"：

- `strict` required_status_checks：合入前 PR 必须基于最新 master；
- `required_linear_history`：只允许 squash 合并，merge commit 被拒绝。

因此多个 PR 各自从 master 独立开枝、串行 squash 时，后合入的 PR 一旦与已合入的
PR 改了同一文件，就必须 rebase + force-push + 重跑 CI。

**真实教训（本 milestone 实测）**：

- Slack milestone：#304/#307/#308/#309 都改 `docs/slack.md`、`design/slack.md`、
  `SlackConnectionRoutes.cs`，各自从 master 独立开枝 → 串行 squash 逐个冲突、逐个
  rebase，费时（本约定的由来）。
- 本 milestone：#312/#314/#315/#313-316 多个 PR 各自从 master 独立开枝；#341 合入
  master 后，#340 立即落后，要 rebase + force-push + 重跑 CI。

## 默认策略

### 1. 开枝前先画文件重叠图

开枝前（issue 规划时）列出本次 PR 将修改的文件，与飞行中（已开 PR / 已开分支）的
改动求交集：

- 无重叠 → 各自从 master 独立开枝，并行推进。
- 有重叠（同一文件）→ 走 stack 或单一集成分支（下节）。

重叠图同时就是**合并顺序图**：按图固定合并顺序，不要"谁先 ready 谁先合"。

示例（Slack milestone 实测，server 路径省略 `packages/server/src/Mohist.Server/` 前缀）：

| PR | 改动文件（重叠热点） |
|---|---|
| #304 | `docs/slack.md`、`design/slack.md` |
| #307 | `docs/slack.md`、`Api/SlackConnectionRoutes.cs` |
| #308 | `Api/SlackConnectionRoutes.cs`、`Infrastructure/Slack/`（outbox 与投递） |
| #309 | `docs/slack.md`、`design/slack.md`、`Api/SlackConnectionRoutes.cs`、`Infrastructure/Slack/`、`docs/cli-reference.md` |

热点：`docs/slack.md` 三路重叠（#304/#307/#309）、`SlackConnectionRoutes.cs` 三路
（#307/#308/#309）、`design/slack.md` 两路（#304/#309）。串行 squash 必撞。

### 2. 有重叠/依赖的 PR：stack 或单一集成分支

**stack**：PR n+1 基于 PR n 的分支开枝，各自开 PR 进 master，合并顺序固定为栈底
优先。每个 PR 只与相邻 PR 共享重叠面，冲突面收窄到相邻两级；栈底合入 master 后，
其上的 PR 自动缩小 diff；需要 rebase 时逐级进行，一次只处理相邻两级。

**单一集成分支**：所有相关 PR 的变更先合进一个集成分支，从集成分支开一个 PR 进
master。适合 PR 数量多、同一文件被多个 PR 反复修改、或变更互相纠缠的场景。

选择：

- 2–3 个 PR、重叠面小 → stack。
- PR 多、重叠密集、变更纠缠 → 单一集成分支。

### 3. spec 先入主干

spec/文档 PR（`docs/`、`design/`）先合 master；issue 实现分支基于**含 spec 的
master** 开枝，不基于 spec 分支、不基于旧 master。文档冲突在 spec 合并时一次解决，
实现分支开枝时 spec 已定稿，实现与 spec 不会并行漂移。

## 合并纪律

- PR 落后 master 时用 rebase，不用 merge master 进来——`required_linear_history`
  拒绝非线性的 merge commit。
- rebase + force-push 会重跑 CI：这正是重叠图 + 固定合并顺序要避免的成本。

## Status

- 约定即目标状态，无自动 enforcement：重叠图靠开枝前人工绘制，未做 CI 自动检测
  文件重叠。自动化（如 PR 打开时提示文件重叠）是可选后续方向。
