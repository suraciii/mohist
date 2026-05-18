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

## 端到端用户旅程

Epic 的用户不是在执行一个任务，而是在持续推进一个长期产品目标。完整旅程应该是：

```text
1. 用户意识到一个长期目标
   -> 2. 创建 Epic
      -> 3. 用 Explore / 手工方式澄清目标
         -> 4. 关联已有 issues / 创建新 issues
            -> 5. 日常回来看整体进展
               -> 6. 子 issue 阻塞时，Epic 暴露目标级影响和 next action
                  -> 7. 目标范围变化，Epic 调整 scope / child issues
                     -> 8. 子 issue 持续交付
                        -> 9. 用户判断 success criteria 是否满足
                           -> 10. Epic Done / Closed / Archived
```

### 1. 创建 Epic

用户不是从“我要建一个容器”开始，而是从长期目标开始：

```text
Create Epic

Title:
  Workflow runtime model cleanup

Goal:
  让 Mohist workflow 的 task/check/run 模型清晰、可恢复、可观察。

Why:
  当前 workflow 相关 issue 越来越多，用户无法判断整体改造进度。

Success Criteria:
  - 用户能看到真实 stage tasks/checks
  - workflow run 有明确状态投影
  - retry/rerun/resume 语义清晰
  - check 只检查，task 负责执行

Non-Goals:
  - 不做完整 workflow.yaml DSL
  - 不做 fallback chain
```

用户此时想要的是把长期目标说清楚，而不是启动 agent 写代码。

### 2. 关联和拆分

创建后，Epic 应帮助用户把目标组织成可执行 issue：

```text
Epic #12 Workflow runtime model cleanup

Suggested / linked issues:
  ✅ #181 show real stage task list
  ✅ #182 materialize WorkflowRun
  ✅ #199 split Build dynamic tasks
  ○ #202 generic workflow event system
  ○ #224 task owns session retry
```

用户需要能做这些动作：

- Add existing issue
- Create child issue
- Remove issue from epic
- Mark issue as out of scope
- Reorder / group issues

### 3. 日常回访

用户可能一天后、一周后回来，只想 5 秒内知道：

```text
这个长期目标现在怎么样？
卡在哪里？
我需要做什么？
下一步推进哪个 issue？
```

Epic 首页应直接回答：

```text
┌─ Epic #12: Workflow runtime model cleanup ───────────────┐
│ Status: Active                                            │
│ Progress: 6/10 delivered                                  │
│ Health: Blocked                                           │
│ Next Action: Resolve #226 completion evidence blocker      │
├───────────────────────────────────────────────────────────┤
│ Delivered                                                 │
│   ✅ #181 show real stage task list                        │
│   ✅ #182 materialize WorkflowRun                          │
│   ✅ #199 split Build dynamic tasks                        │
│                                                           │
│ In Progress                                               │
│   🔄 #226 require completion evidence                      │
│                                                           │
│ Blocked                                                   │
│   ❌ #224 task owns session retry                          │
│                                                           │
│ Planned                                                   │
│   ○ #202 generic Workflow event system                     │
└───────────────────────────────────────────────────────────┘
```

### 4. 处理阻塞

当某个子 issue 阻塞，Epic 不应该只显示红点。它要解释对长期目标的影响：

```text
Blocked

Issue:
  #226 require completion evidence

Impact:
  Blocks success criterion:
  "workflow run 有明确状态投影"

Next Action:
  Review #226 failure and decide whether to retry or split scope.
```

用户在 Epic 里看到的是目标级影响，而不是某个 issue 的底层错误。

### 5. 范围变化

长期目标一定会变化。Epic 必须允许用户管理 scope：

```text
Add to scope:
  #231 workflow yaml first-class definition

Remove from scope:
  #202 generic event system
  reason: too broad for this epic

Split out:
  Provider credential migration
  reason: separate product direction
```

这说明 Epic 需要 `Scope / Non-Goals / Decisions`，否则它会无限膨胀。

### 6. 判断完成

Epic Done 不能只是“所有 child issues done”。更准确是：

```text
All required success criteria are satisfied.
Delivered issues provide evidence.
No blocking planned issue remains required for this goal.
User explicitly marks epic done.
```

Completion Review 应显示：

```text
Success Criteria
  ✅ 用户能看到真实 stage tasks/checks       evidence: #181
  ✅ workflow run 有明确状态投影             evidence: #182
  ✅ retry/rerun/resume 语义清晰             evidence: #178
  ○ workflow.yaml 支持                       moved to future epic

Decision:
  Mark Epic Done
```

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

## 奥卡姆剃刀后的核心模型

前面的探索一度引入了 SuccessCriterion、EpicDecision、EpicHealth、EpicNextAction 等更细的概念。第一版不应该这样做。用户真正需要的是：

```text
我有一个长期目标。
它下面有一些 issue。
我想知道这些 issue 交付到哪里了。
我想知道下一步该看哪个。
```

因此核心概念收缩为：

```text
Epic
  有名称和描述的长期目标

Issue
  现有可执行工作单元

EpicIssue
  Epic 和 Issue 的关联

Progress
  从关联 issue 状态投影出来的只读摘要
```

`Progress` 不需要作为独立实体持久化。它可以由查询时聚合得到：

```text
deliveredCount
totalIssueCount
blockedIssues
activeIssues
nextIssue
```

先不要建模：

```text
EpicDecision
EpicHealth
EpicNextAction
独立 SuccessCriterion 实体
复杂事件流
多层 scope history
复杂 completion review
owner / role / milestone / group
```

这些只有在真实使用中出现明确痛点后再加。

## 最小领域模型

```text
Epic
  id
  number
  projectId
  title
  description
  status: active | done | closed
  priority
  createdAt
  updatedAt
  closedAt?

EpicIssue
  epicId
  issueId
  position
  required
  createdAt
```

Issue 保持现有 workflow，不增加新的 Epic stage。

Epic 进度从子 issue 自动投影：

```text
delivered = issue.stage == done
         && issue.status == completed
         && issue.mergeState == merged
```

Next action 也先作为简单投影，不作为领域对象：

```text
如果有 blocked issue -> next = 第一个 blocked issue
否则如果有 active issue -> next = 第一个 active issue
否则如果有 backlog issue -> next = 第一个 backlog issue
否则 next = Ready to mark done
```

Epic Done 也不要自动判断。第一版只提供进度辅助，最终由用户手动标记 done。

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
- progress / next issue 自动投影
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
- 独立 success criterion 实体
- 决策历史 / scope history
- 自动完成判断

## 产品原则

1. Epic is a named collection of issues with progress.
2. Issue still executes work.
3. Epic never runs workflow.
4. Epic progress is projected from issues, not manually edited.

## 开放问题

- MVP 是否只允许一个 issue 属于一个 primary epic？
- Epic 是否需要独立 priority，还是从子 issue 聚合最高 priority？
- Explore crystallize 到 Epic 是否应和 create issue 并列出现？
