# Multi-Repo Workspace Issue 产品形态

## 探索背景

Mohist 当前 issue 基本绑定一个 git repository：issue 属于一个 project，启动后创建一个 worktree，后续 workflow、artifact、diff、check、merge 都围绕这个单仓库 worktree 展开。

但真实产品需求经常跨多个仓库：例如一个 OAuth 登录能力可能同时需要修改 API 服务、SDK、Web App、文档和部署配置。用户希望把它当成一个产品目标来追踪，而不是在多个仓库里手动维护分散的 issue。

探索目标是想清楚：跨仓库需求在 Mohist 里应该是什么产品形态，以及如何在未来通用 `workflow.yaml` 下保持成立。

## 关键结论

多 repo 功能不应该把 `repo lane` 做成核心执行模型。

更准确的形态是：

```text
Workspace Issue
  = 用户目标 / 跨仓库需求 / 编排层

Repository Issue
  = 某个 repository 内的可执行工作单元
```

Repo lane 可以作为 workspace issue 页面上的视觉投影，但不是领域核心对象。

原因是不同 repo 很可能拥有不同 workflow：

```text
api-server:
  design -> implement -> test -> deploy-ready

sdk:
  implement -> typecheck -> package-ready

web-app:
  implement -> build -> e2e -> deploy-ready

docs:
  write -> review-ready
```

如果强行把所有 repo 投影到同一套 stage 矩阵里，会产生大量空状态、伪状态和不自然的阶段映射。更重要的是，每个 repo 都有独立 worktree、agent session、rebase、retry、check、merge 和 failure recovery。这些能力本质上已经是 issue 的能力。

因此：

```text
Repo lane = UI projection
Repo issue = execution unit
Workspace issue = orchestration unit
```

## 产品形态

```text
┌─ Workspace Issue #240: Add OAuth login ─────────────────────────────────────┐
│ Status: Coordinating                                                        │
│ Progress: 1/4 repo issues ready                                             │
│ Next Action: Fix sdk #42 contract mismatch                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│ Child Repo Issues                                                           │
│                                                                             │
│ ┌────────────┬───────┬──────────────────────────────┬──────────┬──────────┐ │
│ │ Repository │ Issue │ Title                        │ Workflow │ Status   │ │
│ ├────────────┼───────┼──────────────────────────────┼──────────┼──────────┤ │
│ │ api-server │ #18   │ Add OAuth callback endpoint  │ backend  │ Ready    │ │
│ │ sdk        │ #42   │ Add OAuth client types       │ package  │ Blocked  │ │
│ │ web-app    │ #73   │ Add login callback page      │ frontend │ Running  │ │
│ │ docs       │ #12   │ Document OAuth setup         │ docs     │ Planned  │ │
│ └────────────┴───────┴──────────────────────────────┴──────────┴──────────┘ │
├─────────────────────────────────────────────────────────────────────────────┤
│ Global Checks                                                               │
│   ❌ API schema matches SDK LoginResult                                      │
│   ○ Web uses SDK OAuth client                                                │
│   ○ Delivery order safe                                                      │
├─────────────────────────────────────────────────────────────────────────────┤
│ Delivery Plan                                                               │
│   1. api-server #18                                                         │
│   2. sdk #42                                                                │
│   3. web-app #73                                                            │
│   4. docs #12                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
```

用户心智：

```text
我推进的是一个 workspace issue。
Mohist 帮我创建、关联、跟踪多个 repo issue。
每个 repo issue 使用自己的 workflow。
workspace issue 负责整体协调、跨仓库验证和交付顺序。
```

## Workspace Issue 的职责

Workspace issue 不直接修改代码。它负责：

- 定义用户目标和整体范围
- 拆分或关联 child repo issues
- 维护跨仓库契约
- 跟踪 repo issue 的状态和 readiness
- 运行 global checks
- 决定 delivery order
- 跟踪最终交付状态
- 暴露 next action

Workspace issue 的 workflow 是 orchestration workflow，而不是 coding workflow：

```text
Define Scope
  -> Create / Link Repo Issues
  -> Wait For Repo Readiness
  -> Run Global Verification
  -> Deliver In Order
  -> Done
```

这些 stage 名称只是示例。未来应来自 workflow definition，而不是写死在产品里。

## Repository Issue 的职责

Repository issue 是某个 repo 内的真实执行单元。它拥有 Mohist 现有 issue 的核心能力：

- repository
- workflow run
- worktree
- branch
- agent sessions
- artifacts
- tasks
- checks
- diff
- review
- merge state
- retry / rerun / rebase / recovery

每个 repo issue 可以运行自己的 `workflow.yaml`：

```text
api-server #18 -> backend-service.yaml
sdk        #42 -> package-library.yaml
web-app    #73 -> frontend-app.yaml
docs       #12 -> docs.yaml
```

这自然解决了不同 repo workflow 不一致的问题。

## 与 Repo Lane 方案的区别

Repo lane 方案的问题：

```text
Workspace Issue
  api-server lane
  sdk lane
  web-app lane
```

如果 lane 只是展示状态，这是可行的。但如果 lane 要承担 worktree、workflow、tasks、checks、session、diff、merge、retry，它就已经变成了 issue，只是换了名字。

这会带来两个问题：

- 重复实现 issue 能力
- 用户概念不一致：单仓库工作叫 issue，跨仓库里的单仓库工作却叫 lane

因此领域模型应选择 child repo issue，UI 可以把 child issue 渲染成 lane。

## 与 Epic 的区别

Workspace issue 不是 Epic。

```text
Epic:
  长期目标，多个 issue 可以独立交付，时间跨度长。

Workspace Issue:
  一个原子跨仓库需求，child repo issues 为同一个交付目标服务。
```

判断标准：

```text
如果 child issues 可以独立上线、独立产生用户价值：
  用 Epic

如果 child issues 必须组合起来才构成一个需求：
  用 Workspace Issue
```

示例：

```text
Epic:
  OAuth capability rollout
    - Add OAuth login
    - Improve OAuth error UX
    - Add OAuth audit logging

Workspace Issue:
  Add OAuth login
    - api-server issue
    - sdk issue
    - web-app issue
    - docs issue
```

## Check / Verification 形态

跨仓库需求必须有 global checks。单个 repo issue 的本地 checks 只能证明该 repo 自己健康，不能证明多个 repo 组合后成立。

Workspace issue 的 global checks 应覆盖：

- 跨仓库契约一致性
- 交付顺序是否安全
- repo issues 是否达到要求的 target state
- 是否发生部分交付以及后续恢复路径

示例：

```text
Global Checks
  ❌ API schema matches SDK LoginResult
  ○ Web uses SDK OAuth client
  ○ Delivery order safe
```

Repo issue 继续保留本地 checks：

```text
api-server #18
  ✅ build
  ✅ tests
  ✅ review
  ✅ merge-ready

sdk #42
  ✅ build
  ✅ tests
  ❌ package typecheck
```

## Delivery / Integrate 形态

跨 repo delivery 不是原子事务。Workspace issue 必须诚实展示 delivery effects。

```text
Delivery Plan
  1. api-server #18
  2. sdk #42
  3. web-app #73
  4. docs #12

Delivery State
  ✅ api-server #18 merged: abc123
  ✅ sdk #42 merged: def456
  ❌ web-app #73 merge failed
  ○ docs #12 not started

Meaning:
  Partial delivery happened.
  Backend and SDK are already on mainline.
  Web UI is not delivered.
```

Workspace issue 的 next action 应明确指向具体 child issue：

```text
Next Action:
  Resolve web-app #73 merge conflict and continue delivery.
```

## 关键领域模型

```text
Workspace
  id
  repositories[]

Issue
  id
  number
  type: workspace | repository
  workspaceId
  repositoryId?
  parentWorkspaceIssueId?
  workflowRunId

WorkspaceIssueChild
  workspaceIssueId
  repositoryIssueId
  repositoryId
  role: required | optional
  targetState
  deliveryOrder

GlobalCheck
  workspaceIssueId
  name
  involvedIssueIds[]
  status
  output

DeliveryStep
  workspaceIssueId
  repositoryIssueId
  order
  status
  mergeEvidence
```

## 产品原则

1. Workspace issue coordinates; repository issue executes.
2. Repo lane is a UI projection, not the execution model.
3. Each repository issue may run its own workflow definition.
4. Global checks verify cross-repo correctness; repo checks verify local correctness.
5. Delivery effects must be explicit; partial delivery must not be hidden.
6. User actions should stay workspace-level but point to concrete child issue work.

## MVP 范围

第一版可以聚焦：

- workspace issue 类型
- 创建 / 关联 child repo issues
- workspace issue 详情页展示 child issue 表格
- global checks 的结果展示
- delivery plan / delivery state 展示
- repo issue 详情页显示 parent workspace issue
- next action 指向具体 child repo issue

暂不做：

- 自动复杂拆分所有 repo work
- 跨仓库原子回滚
- 多层 workspace issue 嵌套
- 隐藏 child issue，只显示 lane
- 复杂 DAG delivery scheduler
- deployment orchestration

## 开放问题

- Workspace issue 是否也应该使用普通 issue number，还是单独编号？
- Child repo issue 是否允许被多个 workspace issue 关联？
- Global checks 是 workspace workflow 的 check，还是独立 check suite？
- Delivery step 是触发 child issue integrate，还是只等待 child issue 完成后做最终确认？
- Explore crystallize 时是否可以直接 crystallize into workspace issue？
