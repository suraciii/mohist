# Epic Usability Domain Model

## 探索背景

#228 已经交付 Epic MVP：Epic 是 first-class resource，可以创建、列出、查看，并关联 issue。随后观察真实 CLI/Web 后发现，Epic 已经能组织 issue，但还不够舒服、可信地作为长期目标推进面。

本次探索围绕新建 Epic `Epic usability follow-ups` 及其 child issues #242-#245，目标是用领域分析收敛后续实施边界。

## 关键发现

Epic 后续改进应遵循奥卡姆剃刀。不要把 Epic 扩展成通用项目管理系统，也不要因为 UI 需要搜索、确认、提示，就引入额外领域对象。

最小模型只有三个概念：

```text
Epic
  长期目标容器
  active | done | closed

EpicMembership
  Epic 与 Issue 的关联
  一个 Issue 最多属于一个 primary Epic

EpicProgress
  从 linked issues 投影出来的只读结果
```

领域不变量：

```text
Issue executes work.
Epic tracks goals.
Epic does not run workflow.
Epic progress is read-only projection.
Epic lifecycle never mutates child issues.
One issue belongs to at most one primary Epic.
```

## 可视化

```text
Epic = goal
  |
  +-- Membership = linked issues
  |
  +-- Progress = projected delivery

Issue = executable work
  |
  +-- Workflow / worktree / merge / delivery
```

## 决策与结论

四个 child issues 应保持为最小模型上的可用性切片：

- #242：让 Epic 有可用身份。Epic number 是 Epic 的属性，不是独立领域实体。
- #243：让 EpicProgress 投影可信。不要新增 DeliveryFact 实体，只修 projection rule。
- #244：让 EpicMembership 维护可用。Candidate/unavailable issue 是 UI 状态，不是领域对象。
- #245：让 Epic 自身可维护。Editable metadata / lifecycle decision 是 Epic 的属性和动作，不是新领域实体。

实施时应避免引入：

```text
NestedEpic
Roadmap
Milestone
EpicWorkflowRun
EpicApproval
EpicWorktree
WeightedProgress
MultiEpicMembership
DecisionHistory
```

## 开放问题

- #242 实施后，Epic Web route 是否应从 UUID route 迁移到 number route，还是先兼容两者。
- #244 的搜索是否只在前端过滤已有 issue list，还是需要后端搜索 API；这属于 Plan 阶段取舍，不应提前写死到 issue body。
