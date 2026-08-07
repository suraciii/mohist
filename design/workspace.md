---
status: wip
---

# Workspace

Workspace 是 Project 下的一等命名执行环境资源：持久的目录与一组仓库访问权，
生命周期独立于任何 AgentSession 与 WorkflowRun。

边界：Workspace 只持有身份、来源、仓库引用、状态与物化路由事实。目录内容、
git 组织（clone / branch / worktree）是 Agent 的行为，不是平台的 schema。

Workspace 是“工作”的家，仓库是材料：仓库检出位于 workspace 之下（约定 `repos/`），
计划、调研等工作产物属于 workspace 层。这层语义由 prompt 约定承载，不进入平台
schema；它使跨仓库工作的产物始终有不属于任何单一仓库的安放之处。

参考对应（仅帮助理解，不是术语来源）：Runner ≈ Node，WorkflowRun ≈ Pod，
AgentSession ≈ Container，Workspace ≈ local PersistentVolume——生命周期独立于
消费者、物化位置决定调度亲和、随节点丢失、同一消费者组内共享。

## Model

```text
Project.Workspace
  Name                 # Project 内唯一；默认从 Origin 派生
  Origin               # 来源绑定，见下
  RepositoryNames[]    # 对 Project Repository 资源的引用（访问授权）
  Status               # active | archived
  Home                 # 物化路由事实：runnerId + path；未物化时为空
```

Origin 是 Workspace 的创建来源与唯一解析键：

```text
Origin = { kind: issue, issueNumber }
       | { kind: slack, teamId, channelId }
       | { kind: web,   conversationId }
       | { kind: manual }
```

- 同一 Project 内，同一 Origin 同时最多一个 active Workspace。
- workspace 创建与归档发射 `com.mohist.workspace.created` / `com.mohist.workspace.archived`
  事件，谱系见 [`event-protocol.md`](event-protocol.md) 的 workspace 族。
- workflow 路径的反向解析走 Issue：Issue 持有 WorkspaceName，Workspace 不重复
  持有 issue 引用之外的状态。
- AgentSession 持有 WorkspaceName；Workspace 不持有 session 列表。"当前绑定哪些
  session" 是对 Session 的查询结果。
- RepositoryNames 是访问授权与默认检出目标，不代表已物化的 checkout；clone 由
  Agent 在目录内自助完成。

## Semantics

### 创建（动态供应）

Workspace 在 Origin 首次需要执行时被动态创建，没有独立的全局创建流程：

- workflow 路径：Issue 首次启动 run 时创建 `Origin = { issue, n }`，Name 派生为
  `issue-<n>`；retry / rerun 复用同一 workspace。
- 交互路径：入口上下文（Slack channel、Web 对话）的首条触发创建对应 Origin，
  Name 从上下文派生并在 Project 内唯一化。
- manual：`mo workspace create <name>` 显式创建，`Origin = { manual }`。

创建只建立实体与仓库引用；目录在首次调度时由 Runner 物化。manual 名称由用户
提供，Project 内唯一。

### 仓库成员

- `mo workspace repo add/remove <name> <repo>` 修改 RepositoryNames；workspace 存在
  活跃绑定 session 时拒绝修改，错误给出停止会话或等待的下一步。
- workflow 路径初始成员 = Issue 的 RepositoryName；复合 Issue 跨仓库交付经
  `repo add` 挂载（attach 时机见 Status 开放问题）。
- 物化时 Runner 按 RepositoryNames 为 workspace 注入仓库访问凭据，与 workflow
  prepare 复用同一注入通道（`GH_TOKEN` / git credential，见
  [`github-integration.md`](github-integration.md)）；Agent 自助 clone 不感知凭据细节。

### 绑定与解析

- workflow task dispatch：经 Issue 持有的 WorkspaceName 绑定。
- root session 启动：经入口上下文解析 Origin → active workspace；无则动态创建。
  Slack 路径的 workspace 归属被触发 Agent 的 Project；同一 channel 被不同 Project
  的 Agent 使用时，各 Project 持有各自独立的 workspace。
- 被邀请或被委托产生的 session：继承父 session（或所在入口）的 workspace。
- 显式覆盖：`mo agent launch <agent> --workspace <name>` 把新 session 绑定到既有
  workspace（manual 场景的唯一显式入口）；省略 `--workspace` 时 session 不绑定任何
  workspace，Runner 使用默认工作目录，无跨会话连续性，也不创建 workspace 实体。

### 调度亲和与重新物化

- Workspace 一旦在 runner R 物化，绑定它的后续 dispatch 一律路由到 R。
- R 不可达或目录已被回收时，Workspace 重新物化到可用 runner：改写 Home，从空
  目录重新开始。未推送的 git 状态与未落盘产物随旧目录丢失；平台不承诺目录
  连续性。
- workflow 路径的恢复语义不变：profile 的 push 纪律是恢复点，prepare 在新目录
  重新 clone / checkout。

### 初始化

- workflow 路径：干净初始化。prepare 从仓库资源全新 clone；并行 Issue 的
  workspace 目录互不相邻，无共享 checkout / 依赖缓存。
- 交互路径：空目录 + 仓库访问权。Agent 按约定自助组织，平台不预建任何结构。

### 归档

归档是 Workspace 唯一的终态操作：

- workflow 路径：Issue 到 done / cancelled 时自动归档。
- 交互路径：显式 `mo workspace close <name>`；入口消亡（如 Slack channel 归档）
  触发归档。Origin 随归档解除占用：该 channel 的下一条触发动态创建全新 workspace。
- `mo workspace close` 拒绝仍有活跃绑定 session 的 workspace，错误给出停止会话或
  等待的下一步；Origin 为 issue 的 workspace 不受理手动 close（指引 `issue done /
  close`），其归档只由 Issue 终态触发。
- 归档后：实体保留可查，禁止新绑定，Runner 获得目录回收授权。

### Runner 侧目录回收

Runner 保留现有 retention / storage budget 周期维护；回收守卫从"WorkflowRun
终态"改为 Workspace 视角：

| Workspace 状态 | 目录处理 |
|---|---|
| active 且有活跃绑定 session | 禁止回收 |
| active 但无活跃绑定 | 可按磁盘策略回收；实体存活，下次绑定重新物化 |
| archived | 按回收授权删除 |

每次删除仍需取得该目录的 Runtime removal fence——现有不变量不变。

### Prompt 锚定

Runner 为绑定 workspace 的执行注入工作目录锚定段：绝对路径 + "workspace 文件都
在这里，不要搜索 `$HOME`"。目录内部布局约定由 AGENTS.md / prompt 承载，不进入
平台 schema。

## Status

- 本文取代 `repositories.md` 中"Workspace 无独立业务身份、由 WorkflowRunId 标识"
  的旧教条。旧模型的 worktree 物化、WorkspaceRegistry 与终态回收语义由本模型
  重新承接：回收守卫改为上表，Registry 条目身份改为 WorkspaceName。
- 开放问题：复合 Issue 多仓库 attach 的时机；workflow 干净初始化与现有
  `workspace-prepare` action 的衔接；Slack channel 归档事件到 workspace 归档的接线；
  workspace 重新物化后绑定 session 的 Runtime Binding 重指语义；workflow 的 openspec 产物
  位置——现位于 repo 检出内部（`openspec/changes/issue-<n>/`），是否随“工作产物属于
  workspace 层”迁到 workspace 根（复合 Issue 场景下该产物不属于任何单一仓库）。
- 已退役：subagent 的 Managed worktree（交付增量 4）概念——git worktree 属 git 范畴，
  不是平台概念；spawn 的目录来源统一为继承 parent workspace 或绑定命名 Workspace。
