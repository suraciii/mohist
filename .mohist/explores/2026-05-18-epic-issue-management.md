# Epic Issue 管理产品形态

## 探索背景

Mohist 当前的 issue 是可执行工作单元：它进入 Backlog、Plan、Build、Check、Integrate、Done，并由 agent 执行、审查、合并。这个模型适合追踪一个明确任务，但不适合追踪长期产品功能、系统性重构或跨多个 issue 的能力建设。

用户需要一种更高层的产品目标管理方式，用来回答：

- 这个长期目标到底是什么？
- 已经交付了什么？
- 还剩什么？
- 现在卡在哪里？
- 下一步应该推进哪个 issue？
- 什么时候算完成？

## 关键发现

Epic 不应该实现为 issue 的一种 subtype，也不应该通过 label 伪装出来。

原因是 Epic 和 Issue 的生命周期不同：

```text
Issue:
  Backlog -> Plan -> Build -> Check -> Integrate -> Done

Epic:
  Draft -> Active -> Done / Closed
```

Issue 是执行单元，会 start、跑 agent、创建 worktree、经历审批和合并。Epic 是长期目标容器，不执行代码工作，不进入 Plan/Build/Check/Integrate，也不拥有 worktree。

如果把 Epic 做成 issue subtype，会导致用户概念混乱：

- Epic 要不要 start？
- Epic 要不要 Plan/Build/Check？
- Epic 有没有 worktree？
- Epic done 是自己完成，还是子 issue 全部 delivered？
- Epic blocked 是自己的状态，还是子 issue 聚合？
- Epic approval 是审批整体目标，还是审批某个实现？

这些问题说明 Epic 应该是 Mohist 的 first-class feature，但通过关系连接现有 issue。

## 产品形态

Epic 是 Mohist 面向长期产品功能和重构工作的目标层。它不替代 issue，也不参与 workflow 执行；它通过组织 issue、聚合交付状态、暴露阻塞和下一步行动，帮助用户持续推进一个长期目标直到可验收完成。

```text
┌─ Epic #12: Workflow runtime model cleanup ──────────────┐
│ Status: Active        Progress: 6/10 delivered           │
│ Health: Blocked       Next Action: Resolve #226          │
│                                                         │
│ Goal                                                    │
│   让 workflow 的 task/check/run 模型清晰、可恢复、可观察。 │
│                                                         │
│ Success Criteria                                        │
│   - 用户能看到真实 stage tasks/checks                    │
│   - retry/rerun/resume 语义清晰                          │
│   - workflow run 有单一运行态投影                         │
│                                                         │
├─ Delivery Map ──────────────────────────────────────────┤
│ Delivered                                               │
│   ✅ #181 show real stage task list                      │
│   ✅ #182 materialize WorkflowRun                        │
│                                                         │
│ In Progress                                             │
│   🔄 #226 require completion evidence                    │
│                                                         │
│ Blocked                                                 │
│   ❌ #224 task owns session retry                        │
│                                                         │
│ Planned                                                 │
│   ○ #202 generic Workflow event system                   │
└─────────────────────────────────────────────────────────┘
```

Epic 详情页的主视图不应该是 Kanban。Kanban 回答的是每个 issue 分别在哪个 workflow stage；Epic 回答的是长期目标整体交付到哪里了。因此 Epic 详情页应使用 Delivery Map，按交付状态组织子 issue。

## 页面信息架构

### Epic 列表页

Epic 列表页回答：我现在有哪些长期目标在推进，哪个最需要注意？

```text
Active Epics

#12 Workflow runtime model cleanup
  6/10 delivered · 1 blocked · next: #226

#15 Session UI redesign
  3/7 delivered · healthy · next: start #231

#18 Provider credential storage
  0/4 delivered · draft · needs breakdown
```

排序优先级：

1. blocked epic
2. waiting user action epic
3. active epic
4. draft epic
5. done / closed epic

### Epic 详情页

Epic 详情页应围绕六个问题组织：

- Goal：这个长期目标要交付什么用户价值？
- Success Criteria：什么时候算完成？
- Delivery Map：哪些 issue 已交付、进行中、阻塞、计划中？
- Next Action：现在最该推进什么？
- Scope / Non-Goals：哪些属于范围，哪些明确不做？
- Decisions：过程中形成了哪些关键产品或架构决策？

### Issue 详情页

Issue 仍然是 Mohist 的执行单元。Issue 侧只需要显示所属 Epic 的轻量上下文：

```text
Part of Epic #12: Workflow runtime model cleanup
Related issues in this epic: #181, #182, #226
```

这样用户从单个 issue 可以回到长期目标，不会迷失在局部任务里。

## 关键领域模型

```text
Epic
  id
  number
  projectId
  title
  goal
  successCriteria
  scope
  nonGoals
  status: draft | active | done | closed
  priority
  createdAt
  updatedAt
  closedAt?

EpicIssue
  epicId
  issueId
  position
  role?
  createdAt
```

Issue 保持现有 workflow，不增加新的 Epic stage。

Epic 进度从子 issue 自动投影：

```text
delivered = issue.stage == done
         && issue.status == completed
         && issue.mergeState == merged
```

Epic 健康状态也应从子 issue 聚合，而不是让用户手工维护：

```text
Healthy   没有明显阻塞
Attention 有待审批、待决策、长期未推进
Blocked   有子 issue blocked 或关键路径断裂
```

## 与现有概念的边界

### Epic vs Issue

Issue executes work. Epic tracks goals.

Issue 是执行单元；Epic 是目标容器。Epic 不 start、不跑 agent、不创建 worktree、不进入 Plan/Build/Check/Integrate。

### Epic vs Label

Label 适合分类和过滤，不适合承载目标、成功标准、进度、阻塞和下一步行动。Epic 不能降级成 label。

### Epic vs Prerequisite

Prerequisite 是执行门控：

```text
Issue #226 启动前必须等 #181 delivered
```

Epic 是目标组织：

```text
#181、#182、#226 一起组成 workflow runtime cleanup
```

Epic membership 不应该控制 issue 能否 start。Epic 页面可以展示依赖关系，但不替代 prerequisite。

### Epic vs Explore

Explore 很适合成为 Epic 的前置讨论形态：

```text
Explore session
  -> crystallize into Epic
  -> create / attach child Issues
```

未来可以支持：

- Explore -> Create Epic
- Explore -> Update Epic
- Epic -> Explore this epic

## MVP 范围

第一版应保持克制：

- Epic CRUD
- Epic 列表页
- Epic 详情页
- 添加 / 移除 child issues
- issue 详情页显示所属 epic
- progress / health / next action 自动聚合
- CLI 支持 `mo epic create/list/show/add/remove/close`

## 非目标

第一版暂不做：

- nested epic
- milestone / gantt
- 多层 roadmap
- epic 自己跑 workflow
- 复杂 owner / 权限
- 自动批量拆 issue
- 多 epic membership
- 用 epic membership 替代 prerequisite

## 产品原则

1. Epic tracks goals, Issue executes work.
2. Epic progress is projected from delivered issues, not manually edited.
3. Every active Epic must show a next action.

## 开放问题

- MVP 是否只允许一个 issue 属于一个 primary epic？
- Epic 是否需要独立 priority，还是从子 issue 聚合最高 priority？
- Epic 的 success criteria 是否需要结构化 checklist，还是先使用 markdown？
- Explore crystallize 到 Epic 是否应和 create issue 并列出现？
