# Issue Breakdown / Sub-issue（WIP — 产品方案未定）

> **状态：WIP，暂不实现。** 原 backlog issue #96 已关闭。当前 Epic 轨道已覆盖 issue 组织/聚合需求；自动 breakdown 在 #281 中被明确列为 Non-Goal。本文件记录产品方案的开放问题，后续有时间再想清楚。

## 背景

用户想把一个较大的 parent issue 拆成多个可独立推进的子工作项。原 issue #96 提议了一套完整方案：

1. **Split Issue**：Agent 分析 parent issue，生成 `issue-breakdown.json` artifact。
2. **Approve Breakdown**：用户审查 artifact。
3. **Apply Breakdown**：确定性 action 创建 sub issues + 持久化 parent/sub IssueLink。

## 为什么暂不实现

### 1. 与 Epic 严重重叠

Epic 已经是 issue 的组织层（`design/domain-analysis.md`："Epic 是 Issue 的组织 facet，同一问题类两个粒度"）。Epic 当前已支持：

- 把多个 issue 关联到一个父主题（`EpicGrain.LinkIssueAsync`）
- 聚合进度（`2/3 done`）
- 自动完成父项（所有 linked issues 终态 → Epic Done）
- 依赖可视化

#96 提议的 `IssueLink(type=child)` 会形成第二套平行的 parent→child 关系，与 Epic membership 重复。

### 2. 团队已在 #281 明确拒绝

`openspec/changes/archive/2026-06-29-issue-281/design.md` 的 Non-Goal 写着：

> No automatic issue breakdown or batch child-issue creation.

这是在 #96 创建两周后做的决定。

### 3. 通用 IssueLink 模型是过度设计

`blocks`/`relates` 类型纯属投机——prerequisites 已覆盖 start-ordering，没有需求要求任意 issue 间链接。7 个 provenance 字段 + 3 个新 action + 2 个 artifact schema 违反"数据模型应该尽可能地简洁"原则。

### 4. artifact 消费链有 gap

"Split task 上传 breakdown artifact → 审批门 → Apply task 消费"假设 runner 能跨审批边界按 id 读取已存储 artifact 注入后续 action。当前 runner（`packages/runner/src/actions/registry.ts`）没有这个机制——`core/artifact-exists` 只检查 workspace 路径文件是否存在。

## 唯一成立的前提

Sub-issue 与 prerequisite 正交：prerequisite 表达 start-ordering（B 不能在 A 完成前开始），sub-issue 表达 origin（B 从 A 拆出）。这两个轴确实不同。

## 后续需要想清楚的问题

1. **Epic vs Sub-issue 的边界**：如果"把一个大 issue 拆成几个小的"就是"建一个 Epic + 几个成员 issue"，那 sub-issue 作为独立概念是否必要？还是说 sub-issue 有 Epic 不覆盖的语义（比如"从这一个 issue 拆出"的 provenance、parent issue 自身也是工作项而非纯组织容器）？
2. **最小可行版本**：如果要做，最简形态是什么？——Agent 生成 breakdown 文档（普通 markdown），用户手动创建 issue 并挂到 Epic？还是确实需要持久化的 parent/sub 关系？
3. **跨审批边界的 artifact 消费**：如果走 artifact 路线，runner 需要先支持"读取已存储 artifact 内容注入后续 action"。
4. **与 #94 batch-link 的协同**：#94 正在给 Epic 加批量关联 issue 的能力。如果拆分需求折叠进 Epic，batch-link 就是落地工具。

## 参考

- 原 issue #96（已关闭）——完整的三段式设计 + IssueLink 模型
- #281（done）——明确拒绝自动 breakdown
- #94（in_progress）——Epic batch-link + Reopen + 搜索排序 + 活动时间线
- #105（backlog）——Epic roadmap owner workbench
- `design/domain-analysis.md`——Epic 是 Issue 组织 facet 的定位
