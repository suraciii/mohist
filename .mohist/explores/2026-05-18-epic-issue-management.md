# Epic Issue 管理产品形态

## 探索背景

Mohist 当前的 issue 是可执行工作单元：它进入 Backlog、Plan、Build、Check、Integrate、Done，并由 agent 执行、审查、合并。这个模型适合追踪一个明确任务，但不适合追踪长期产品功能、系统性重构或跨多个 issue 的能力建设。

用户需要一种更高层的目标管理方式，用来回答：

- 这个长期目标是什么？
- 它下面有哪些 issue？
- 已经交付了多少？
- 当前卡在哪里？
- 下一步应该看哪个 issue？

## 核心结论

Epic 应实现为 Mohist 的 first-class feature，而不是 issue subtype，也不是 label 扩展。

原因是 Epic 和 Issue 的职责不同：

```text
Issue:
  可执行工作单元
  Backlog -> Plan -> Build -> Check -> Integrate -> Done
  可以 start，运行 agent，创建 worktree，经过审批和合并

Epic:
  长期目标容器
  Active -> Done / Closed
  不 start，不运行 agent，不创建 worktree，不进入 workflow stage
```

如果把 Epic 做成 issue subtype，会让用户必须理解一组不成立的问题：

- Epic 要不要 start？
- Epic 要不要 Plan / Build / Check？
- Epic 有没有 worktree？
- Epic approval 是审批目标，还是审批实现？
- Epic done 是自己完成，还是子 issue 全部完成？

这些问题说明 Epic 不应该继承 issue 的执行语义。Issue still executes work. Epic tracks goals.

## 用户端到端场景

Epic 的用户不是在执行一个任务，而是在持续推进一个长期产品目标：

```text
1. 用户意识到一个长期目标
   -> 2. 创建 Epic
      -> 3. 写清 title / description
         -> 4. 关联已有 issue，或后续从 Epic 中创建新 issue
            -> 5. 日常回来看整体进展
               -> 6. 看到阻塞、活跃 issue 和下一步
                  -> 7. 随着范围变化添加或移除 issue
                     -> 8. 子 issue 持续交付
                        -> 9. 用户判断目标已经满足
                           -> 10. 手动 Mark Done 或 Close
```

用户最核心的回访问题是：

```text
这个长期目标现在怎么样？
卡在哪里？
我现在应该看哪个 issue？
```

因此第一版 Epic 不需要完整 roadmap、milestone、gantt、独立成功标准实体或决策历史。它只需要把相关 issue 组织起来，并给出清晰的进度和下一步。

## 产品形态

Epic 是有名称和描述的 issue 集合。它不替代 issue，也不参与 workflow 执行；它通过组织 issue、聚合交付状态、暴露阻塞和下一步行动，帮助用户推进长期目标。

产品原则：

1. Epic is a named collection of issues with progress.
2. Issue still executes work.
3. Epic never runs workflow.
4. Epic progress is projected from issues, not manually edited.

## Web UI 形态

### 导航

在主导航中增加 `Epics`：

```text
Board | Epics | Explore | Archived | Settings
```

Epic 是目标管理入口，不放进 Board 的 workflow lane。

### Epic 列表页

Epic 列表页回答：我现在有哪些长期目标，哪个最需要注意？

```text
+----------------------------------------------------------------+
| Epics                                             [New Epic]    |
+----------------------------------------------------------------+
| Active                                                         |
|                                                                |
| #12 Workflow runtime model cleanup                             |
| 6/10 delivered · next: #226 require completion evidence         |
|                                                                |
| #15 Session UI redesign                                        |
| 3/7 delivered · next: start #231                                |
|                                                                |
| Done                                                           |
|                                                                |
| #8 Provider settings cleanup                                   |
| 5/5 delivered                                                  |
+----------------------------------------------------------------+
```

第一版列表只显示：

- title
- status
- delivered / total
- next issue

不要引入健康分、复杂图表或 roadmap 视图。

### 创建 Epic

创建表单保持简单：

```text
+--------------------------------------+
| New Epic                             |
+--------------------------------------+
| Title                                |
| [Workflow runtime model cleanup    ] |
|                                      |
| Description                          |
| [长期目标说明...]                    |
|                                      |
| Priority                             |
| [P1 v]                               |
|                                      |
|                  [Cancel] [Create]   |
+--------------------------------------+
```

第一版不要求结构化 success criteria。长期目标先由 description 承载，避免过早建模。

### Epic 详情页

Epic 详情页回答：目标、进度、下一步、关联 issue。

```text
+----------------------------------------------------------------+
| Epic #12 Workflow runtime model cleanup      [Mark Done] [Close]|
| Active · P1                                                     |
+----------------------------------------------------------------+
| Progress                                                       |
| 6 / 10 delivered                                               |
|                                                                |
| Next                                                           |
| #226 require completion evidence                               |
+----------------------------------------------------------------+
| Description                                                    |
| 让 workflow 的 task/check/run 模型清晰、可恢复、可观察。          |
+----------------------------------------------------------------+
| Issues                                             [Add Issue]  |
|                                                                |
| Done                                                           |
| [done] #181 show real stage task list                          |
| [done] #182 materialize WorkflowRun                            |
|                                                                |
| Active                                                         |
| [check] #226 require completion evidence                       |
|                                                                |
| Backlog                                                        |
| [backlog] #202 generic workflow event system                   |
+----------------------------------------------------------------+
```

详情页不做 Kanban。Kanban 回答 issue 的 workflow stage；Epic 回答长期目标交付进度。

### Add Issue

从 Epic 详情页添加已有 issue：

```text
+-----------------------------------------------+
| Add Issue to Epic #12                         |
+-----------------------------------------------+
| Search issues                                 |
| [completion evidence                         ]|
|                                               |
| #226 require completion evidence              |
| #224 task owns session retry                  |
|                                               |
|                         [Cancel] [Add Issue]  |
+-----------------------------------------------+
```

第一版只允许一个 issue 属于一个 primary Epic。如果 issue 已属于其他 Epic，应阻止添加并告诉用户原因。

### Issue 详情页

Issue 仍是执行单元。Issue 详情页只显示轻量 Epic 上下文：

```text
Part of Epic: #12 Workflow runtime model cleanup
```

用户可以从局部 issue 回到长期目标，但 issue 的 start、approval、merge、prerequisite 语义不被 Epic 改写。

## 核心领域模型

遵循奥卡姆剃刀，第一版只需要四个核心概念：

```text
Epic
  有名称和描述的长期目标容器

Issue
  现有可执行工作单元

EpicIssue
  Epic 和 Issue 的关联

Progress
  从关联 issue 状态投影出来的只读摘要
```

`Progress` 不需要持久化。它可以查询时从关联 issue 聚合：

```text
deliveredCount
totalIssueCount
blockedIssues
activeIssues
nextIssue
```

最小持久模型：

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
  createdAt
```

第一版不需要 `required` 字段。因为没有结构化 completion review，也不自动判断完成，required 暂时不会影响用户可见行为。

## 投影规则

Epic progress 从子 issue 自动投影：

```text
delivered = issue.stage == done
         && issue.status == completed
         && issue.mergeState == merged
```

Next issue 使用简单规则：

```text
如果有 blocked issue -> next = 第一个 blocked issue
否则如果有 active issue -> next = 第一个 active issue
否则如果有 backlog issue -> next = 第一个 backlog issue
否则 next = Ready to mark done
```

Epic Done 不自动判断。第一版只提供进度辅助，最终由用户手动标记 done。

## 与现有概念的边界

### Epic vs Issue

Issue executes work. Epic tracks goals.

Epic 不 start、不跑 agent、不创建 worktree、不进入 Plan/Build/Check/Integrate。

### Epic vs Label

Label 适合分类和过滤，不适合承载目标、进度、阻塞和下一步行动。Epic 不能降级成 label。

### Epic vs Prerequisite

Prerequisite 是执行门控：

```text
Issue #226 启动前必须等 #181 delivered
```

Epic 是目标组织：

```text
#181、#182、#226 一起组成 workflow runtime cleanup
```

Epic membership 不控制 issue 能否 start，也不替代 prerequisite。

### Epic vs Explore

Explore 可以在未来成为 Epic 的前置澄清方式：

```text
Explore session -> Create Epic -> attach / create child Issues
```

但这不是第一版必需能力。MVP 可以先从手工创建 Epic 和关联 issue 开始。

## MVP 范围

- Epic create / list / detail
- Epic mark done / close
- 添加 / 移除 child issue
- Issue 详情页显示所属 Epic 链接
- Progress / next issue 自动投影
- CLI 支持 `mo epic create/list/show/add-issue/remove-issue/close`

## 非目标

- nested epic
- milestone / gantt / roadmap
- Epic 自己跑 workflow
- Epic 创建 worktree
- 独立 success criterion 实体
- decision history / scope history
- 自动完成判断
- 用 Epic membership 替代 prerequisite
- 一个 issue 同时属于多个 Epic
- Explore crystallize directly into Epic

## 就绪结论

该功能已经可以进入实施。核心需求足够窄：新增一个目标容器，并围绕 issue 关联、进度投影、下一步投影提供 Web + CLI 的最小闭环。

实施时应避免把 Epic 做成通用项目管理系统。只要用户能创建 Epic、关联 issue、查看进度和下一步、从 issue 返回 Epic，就已经交付第一版核心价值。
