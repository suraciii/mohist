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

## Workspace Repository Registry

Mohist 不能靠猜测来知道“有哪些仓库待探索”。跨仓库开发必须先有一个明确的 workspace repository registry。

这个 registry 回答：

```text
这个产品工作区包含哪些 repositories？
每个 repository 在本机的路径是什么？
默认 base branch 是什么？
这个 repository 在产品里的角色是什么？
是否允许 Mohist 在其中创建 issue / worktree / merge？
```

推荐产品形态：

```text
Workspace: openviking-product

Repositories
  api-server
    path: /home/surac/repos/openviking-api
    baseBranch: main
    role: backend-service
    workflow: backend-service.yaml

  sdk
    path: /home/surac/repos/openviking-sdk
    baseBranch: main
    role: package-library
    workflow: package-library.yaml

  web-app
    path: /home/surac/repos/openviking-web
    baseBranch: main
    role: frontend-app
    workflow: frontend-app.yaml

  docs
    path: /home/surac/repos/openviking-docs
    baseBranch: main
    role: documentation
    workflow: docs.yaml
```

Registry 的来源应该以显式配置为主，自动发现为辅：

```text
Primary:
  workspace config 中显式声明 repositories

Secondary:
  用户通过 UI/CLI 添加 repository

Assistive:
  Mohist 可以扫描目录、Git remotes、package workspace、submodules，提出候选项
  但必须由用户确认后才进入 registry
```

原因：自动扫描只能发现“附近有哪些 git repo”，不能可靠判断“哪些 repo 属于这个产品工作区”。如果 Mohist 自动把无关 repo 加进 workspace，会导致 Explore、Plan 和 agent 修改范围失控。

### 用户如何配置

用户第一次启用 multi-repo workspace 时，应该看到一个 setup flow：

```text
Create Workspace

Name:
  openviking-product

Repository Root:
  /home/surac/repos

Detected repositories:
  [x] openviking-api       /home/surac/repos/openviking-api
  [x] openviking-web       /home/surac/repos/openviking-web
  [x] openviking-sdk       /home/surac/repos/openviking-sdk
  [ ] unrelated-sandbox    /home/surac/repos/unrelated-sandbox

For each selected repository:
  name
  path
  base branch
  role
  default workflow
```

CLI 也应支持显式添加：

```text
mo workspace create openviking-product
mo workspace repo add api-server /home/surac/repos/openviking-api --role backend-service --base main
mo workspace repo add sdk /home/surac/repos/openviking-sdk --role package-library --base main
mo workspace repo list
```

### Explore 如何使用 Registry

Explore 不应该从全文件系统搜索。它应该读取 workspace repository registry，把它作为探索边界：

```text
Explore context
  workspace: openviking-product
  repositories:
    api-server -> /home/surac/repos/openviking-api
    sdk        -> /home/surac/repos/openviking-sdk
    web-app    -> /home/surac/repos/openviking-web
    docs       -> /home/surac/repos/openviking-docs
```

当用户提出需求：

```text
Add OAuth login
```

Explore 的工作是：

```text
1. 在 registry 内理解产品结构
2. 读取每个 repo 的 metadata / README / package / workflow / specs
3. 判断候选 affected repositories
4. 解释为什么涉及这些 repo
5. 让用户确认
6. crystallize into workspace issue
```

输出应该类似：

```text
Affected repository proposal

Required
  api-server
    reason: owns OAuth callback API

  sdk
    reason: Web consumes API through SDK types/client

  web-app
    reason: user-facing login entry and callback page

Optional
  docs
    reason: setup instructions need OAuth environment variables

Not included
  infra
    reason: no deployment/env change detected yet
```

用户确认后，workspace issue 才真正获得 child repo issue breakdown。

### 路径可信度

每个 repository path 应有健康状态，防止 Mohist 使用过期路径：

```text
Repository health
  path exists
  is git repository
  base branch exists
  worktree base dir writable
  workflow file exists or fallback workflow available
```

Workspace issue 创建前，如果 registry 有问题，用户应该看到明确提示：

```text
Cannot create workspace issue yet

Repository sdk is unavailable:
  path /home/surac/repos/openviking-sdk does not exist

Fix:
  update repository path
  or remove sdk from this workspace
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

## 端到端用户旅程

用户视角下，multi-repo workspace issue 的体验应该覆盖完整路径：

```text
1. 我有一个跨仓库需求
   -> 2. Mohist 帮我识别影响仓库和拆分 repo issues
      -> 3. 我审批这个拆分是否正确
         -> 4. Mohist 推进各 repo issues
            -> 5. 我回来看整体状态和下一步
               -> 6. Mohist 做跨仓库验证
                  -> 7. 我审批是否可以整体交付
                     -> 8. Mohist 按交付顺序推进
                        -> 9. 我看到最终完成或部分交付后的恢复路径
```

### 1. 创建跨仓库需求

用户从自然语言开始，不应该先被迫理解 repo 拆分：

```text
Create Workspace Issue

Title:
  Add OAuth login

Goal:
  用户可以通过 OAuth 登录，Web/API/SDK 行为一致。

Known affected repositories:
  [x] api-server
  [x] sdk
  [x] web-app
  [ ] docs
  [ ] infra

Mode:
  [Explore first]  [Create directly]
```

如果用户不确定仓库范围，Mohist 应允许先进入 Explore：

```text
用户输入目标
  -> Explore 读取 workspace repository map
  -> 提出 affected repo 建议
  -> 用户确认
  -> crystallize into workspace issue
```

用户此时最关心的是：

- 这个需求是不是一个跨仓库原子需求？
- Mohist 识别的仓库是否正确？
- 有没有遗漏 docs、infra、SDK 这类容易被忘记的仓库？

### 2. Scope / Breakdown 阶段

Workspace issue 的第一段工作不是写代码，而是把跨仓库目标拆清楚：

```text
Workspace Issue #240: Add OAuth login

Scope Proposal
  api-server
    create repo issue: Add OAuth callback endpoint
    target state: merge-ready
    required: yes

  sdk
    create repo issue: Add OAuth client types
    target state: package-ready
    required: yes

  web-app
    create repo issue: Add login callback page
    target state: e2e-passed
    required: yes

  docs
    create repo issue: Document OAuth setup
    target state: reviewed
    required: optional

Cross-Repo Contracts
  API OAuth response must match SDK LoginResult
  Web App must use SDK OAuth client
  Delivery order must keep API backward compatible

Delivery Order
  1. api-server
  2. sdk
  3. web-app
  4. docs
```

用户审批的问题不是“代码对不对”，而是：

- 仓库范围对不对？
- 每个 repo issue 的职责是否清楚？
- target state 是否能表达这个 repo 对整体需求的贡献？
- cross-repo contract 是否完整？
- delivery order 是否安全？

### 3. 创建 / 关联 Repo Issues

用户审批 scope 后，Mohist 创建或关联 repo issues：

```text
Created child repo issues
  api-server #18
  sdk #42
  web-app #73
  docs #12
```

关键体验要求：

- 用户不需要手动到每个 repo 创建 issue。
- 用户可以替换为已有 repo issue。
- 每个 repo issue 都显示 parent workspace issue。
- Workspace issue 立即变成总控视图，而不是跳走到某个 repo issue。

### 4. 执行和回访

用户最常见的行为是“离开后回来扫一眼”。Workspace issue 首页必须在 5 秒内回答：

```text
现在整体在哪？
哪个 repo 卡住？
下一步我需要做什么？
如果不用我做，Mohist 正在推进哪个 repo issue？
```

推荐首页摘要：

```text
Workspace Issue #240: Add OAuth login

Overall:
  Coordinating repo work

Progress:
  1/4 repo issues ready

Blocked:
  sdk #42 contract mismatch

Next Action:
  Retry sdk #42 after updating LoginResult
```

下方再给完整表格：

```text
Child Repo Issues
  api-server #18  Ready
  sdk #42         Blocked: LoginResult mismatch
  web-app #73     Running: e2e workflow
  docs #12        Planned
```

### 5. 局部失败时的用户感知

失败不能只显示 `workspace issue blocked`。用户需要知道：

```text
哪里失败？
为什么失败？
影响哪些 repo？
应该推进哪个 child issue？
```

示例：

```text
Blocked

Problem:
  SDK LoginResult does not match api-server OAuth response.

Affected:
  api-server #18
  sdk #42

Evidence:
  api-server returns: token, refreshToken, user
  sdk expects:     token, user

Next Action:
  Fix sdk #42 and rerun global contract check.
```

这比简单说 “Global check failed” 更符合用户决策。

### 6. Global Verification

当 repo issues 达到各自 target state 后，workspace issue 运行 global checks：

```text
Global Verification

Repo readiness
  ✅ api-server #18 reached merge-ready
  ✅ sdk #42 reached package-ready
  ✅ web-app #73 reached e2e-passed
  ○ docs #12 optional

Cross-repo contracts
  ✅ API response matches SDK LoginResult
  ✅ Web App uses SDK OAuth client
  ✅ Delivery order is safe
```

用户此时审批的是整体交付风险：

- 这些 repo issues 是否作为一个整体成立？
- 是否还有跨仓库契约风险？
- 是否可以按 delivery order 交付？

### 7. Delivery / 部分交付

用户批准后，workspace issue 进入 delivery。它必须显式展示交付副作用：

```text
Delivery

✅ api-server #18 merged: abc123
✅ sdk #42 merged: def456
❌ web-app #73 merge failed: conflict in OAuthCallback.tsx
○ docs #12 not started

Meaning:
  Partial delivery happened.
  Backend and SDK are already on mainline.
  Web UI is not delivered.

Next Action:
  Resolve web-app #73 conflict and continue delivery.
```

这条体验原则非常关键：多 repo delivery 不是事务，Mohist 不能隐藏已经发生的主线变化。

### 8. 完成

Workspace issue Done 的含义不是“所有 child issue 都有某个固定 stage”，而是：

```text
All required child repo issues reached their declared delivery target.
All global checks passed.
All required delivery steps completed.
No unresolved partial delivery remains.
```

Done 页面应该保留汇总：

```text
Delivered
  api-server #18  abc123
  sdk #42         def456
  web-app #73     789abc
  docs #12        skipped optional

Global checks
  API/SDK contract passed
  Web flow passed
  Delivery order passed
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
