---
status: accepted 2026-07-15
---

# 复合 Issue / 子 Issue

产品 spec：[`docs/sub-issues.md`](../docs/sub-issues.md)、[`docs/repositories.md`](../docs/repositories.md)。本文记录领域设计、决策依据与约束。

## 决策沿革

- 原 #96 提议三段式自动 breakdown（Agent 分析 → `issue-breakdown.json` artifact → 审批 → 批量建子 issue），已否决；#281 将自动 breakdown 与批量子 issue 生成列为 Non-Goal，**该 Non-Goal 维持不变**——拆分永远是 owner（或替 owner 操作的外部 agent）的显式决策。
- 上一版结论"sub-issue 与 Epic 重叠，deferred"成立于单仓库 project 前提。2026-07-15 拍板复合 issue 成立，前提变化：project 引入多仓库资源后，"一份需求拆到多个仓库执行"成为真实结构需求。分解轴（一份工作的内部分工）与 Epic 轴（产品目标的组织与供料）正交，不再重叠：
  - Epic 成员是独立有价值的交付物，串行供料控制 WIP。
  - 子 issue 是同一份工作的分工，完成一半没有产品意义，startable 者并行推进。

## 模型

- 子 issue 持有指向父 issue 的单向引用（parent）。只有这一种关系，只有一层（子不能再有子）。**不引入通用 IssueLink**——`blocks`/`relates` 仍然拒绝，start-ordering 由 prerequisite 覆盖，origin 由 parent 覆盖。
- 父 issue 不是新类型："复合"= 拥有 ≥1 个子 issue 这一事实。全部子 issue 解除后回归普通 issue。
- 仓库：project 持有仓库集合（名字唯一 + base branch），恰好一个 default；issue 持有目标仓库名，缺省解析为 default。父 issue 的目标仓库无执行意义（它不进 workflow）。
- 状态汇总（由子状态变化触发重算，非手动维护）：
  - 全部子终态且 ≥1 done → 父 done；全部 cancelled → 父 cancelled。
  - 任一子被 reopen → 已完成的父回到 in-progress。
  - 显式 start 或任一子开始工作 → 父 in-progress。

## 域归属与不变式

- 父子关系、状态汇总、复合推进全部落在 **Issue 子域**（work organization）。
- **Workflow 零感知**：子 issue 的 WorkflowRun 与普通 issue 无异；父 issue 不创建 WorkflowRun。不变式"Issue → Workflow 单向、Workflow 不知道 issue"保持。
- 仓库集合与 default 解析属于 **Project Space**（repo binding 本就归它）；任务派发时目标仓库解析为 repo path + base branch 注入，替代"从 project 取唯一仓库"。
- 父子是不同 Issue 聚合，经领域事件协调（子 issue 终态/reopen 事件 → 父重算并推进兄弟），与 `WorkflowRunCompleted → CompleteIssue` 同风格。见 [`workflow/issue-coordination.md`](workflow/issue-coordination.md)。

## 复合推进

- start 父 = 启动全部 startable 子（backlog 且 prerequisite 满足），并行；子到终态后重评估，启动新解锁者，直到全部终态。
- 复用现有 start 前置约束（并发上限、runner 在线）；无空位时等待下一次重评估，不引入独立调度器状态。
- 手动 start 单个子 issue 始终允许，与复合推进不冲突。

## 与 Epic 的隔离（关键约束）

- **Epic 机制零改动**。Epic 视父 issue 为普通 issue：auto-advance 调用的 start 即触发复合推进；父 done 经由现有进度重算计入。
- **子 issue 拒绝 link 到 Epic**——校验落在 Issue 侧的 link 入口，不改 Epic 的推进与判定逻辑。

## 生命周期约束表

| 操作 | 约束 |
|---|---|
| 挂靠（create `--parent` / update `--parent`） | 子必须 backlog 未启动；父未启动或 in-progress（终态拒绝）；已有子的 issue 不能被挂为子 |
| 解除（`--parent none`） | 任意时刻允许；父汇总立即重算 |
| start 父 | 需 ≥1 子 issue（无子则走普通 workflow start） |
| close 父 | 全部子终态才允许，不级联 |
| archive 父 | 子随父归档；子不单独 archive |
| 删除仓库 | default 不可删；有未终态 issue 绑定不可删 |

## 开放问题（单独立项）

1. **Plan 背景注入**：子 issue Plan 时注入父 issue 标题与 body，依赖 runner 支持"派发输入携带关联 issue 上下文"，实现路径与 [`workflow/task-dispatch.md`](workflow/task-dispatch.md) 对齐。
2. **multi-checkout**：一个 issue 同时检出多个仓库（联调型工作）明确不做；产品 spec 以"最后一个联调子 issue"覆盖，真实需求出现再评估。
3. **Web UI**：看板上父卡片按 status 定位（无 stage）、进度徽标与 blocked 提示的具体形态，归 [`web-ui.md`](web-ui.md) 细化。
