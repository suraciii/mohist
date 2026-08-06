---
status: wip
---

# Agent 受管工作空间（Managed worktree）

子会话可以请求一个**隔离工作空间**：来源由 Project Space 的 Repository 定义、由被 pin 的
Runner 物化、由子 AgentSession 拥有工作目录。它是会话树交付增量 4（Managed worktree）的执行
契约，依赖增量 1 的 link/launch 模型与 Runner pinning。

产品行为见 [`../docs/subagents.md`](../docs/subagents.md) 的「实装差距」。会话树的 spawn、
接受条件、父子 link、terminal callback、cascade stop 与 detach 的完整契约以
[`subagents.md`](subagents.md) 为唯一权威；本篇只定义隔离工作空间的资源身份、物化状态机、
物化/释放契约与生命周期，以及它如何接入 spawn 的 canonical launch pipeline。

## 边界与放置

| 关注点 | 归属 | 说明 |
|---|---|---|
| Repository 资源（gitUrl / baseBranch） | Project Space | 唯一定义工作空间的来源仓库；本篇不新增第二个资源类型 |
| 工作空间请求意图（mode） | spawn 调用面 | 只能是受约束的 `inherit` / `worktree`，不是路径 |
| 父工作空间来源 `WorkspaceRepository` | Session context | 父会话工作空间的 Project Repository 来源；explicit Project-backed launch 经 Runner 校验确认，或 nested managed child 继承确认；未确认/无来源不能作 worktree parent |
| 子 AgentSession.workDir | Session context | 物化成功后固定为物化路径，不可变 |
| 物化权威（worktree 落盘、注册表、回收） | Runner | 唯一接触文件系统者 |
| 物化 binding（哪个 Runner、opaque identity） | Session context | 与 Runtime binding 并列的另一条绑定事实 |
| 物化状态机（Materialize/ReleaseState） | spawn launch plan | durable，coordinator/abort 在 child Session 创建前即可读取 |
| binding / activity 裁判 | Server | 决定 workDir、解析 mode 与来源、签发物化请求；从不读写文件系统 |

- **Server 不碰文件系统**：Server 只持久化 Runner 返回的 path 字符串与 opaque identity，从不构造、
  推导、校验或读写任何工作空间路径。path 的确定性推导（见下）是 Runner 内部的 registry-loss
  恢复机制，不是 Server 的构造依据。
- **Runner 不猜身份、不换 Runner**：Runner 用请求携带的 `ChildSessionId` 与 `WorkspaceIdentity`
  寻址自己物化的 worktree，不从环境、目录扫描或 Runtime Session 反推身份；物化始终在 parent
  binding 的那个 Runner 上本地完成，child AgentJob 也 pin 到同一个 Runner。

### 与既有工作空间概念的边界

| 概念 | 身份 | 配置 | 生命周期触发 | 物化者 |
|---|---|---|---|---|
| Repository | Project 内资源名 | gitUrl / baseBranch / isDefault | 由未完成 Issue 占用锁定 | 不物化，是来源 |
| WorkflowRun workspace | `WorkflowRunId` 推导 | Issue 目标 Repository | WorkflowRun 终态 | Runner（`<runnerRoot>/workspaces/<workflowRunId>`） |
| AgentSession.workDir | Session 不变事实 | launch 时固定 | Session 无终态 | launch 来源决定 |
| Runtime workspace（如 OpenCode directory Instance） | Runtime 物理资源 | directory = workDir | Runtime 自有 | Runtime adapter |
| **Managed worktree（本篇）** | child AgentSession | Project Repository + parent HEAD | 见「生命周期」 | 被 pin 的 Runner（git worktree） |

Managed worktree 不是新的 Project Space 聚合，也不是 Repository 的第二份真源。它是 Runner
的可重建执行状态，身份由 child AgentSession 推导，配置由 Project Repository 决定——这与
WorkflowRun workspace「身份由 `WorkflowRunId` 推导、配置由 Issue Repository 决定」对称。

## Model

### Workspace mode

子会话 spawn 携带一个受约束的工作空间意图，不是路径：

```text
WorkspaceMode
  inherit   默认；child 复用 parent authoritative workDir（增量 1，不物化）
  worktree  child 请求隔离 worktree（增量 4，由被 pin 的 Runner 物化）
```

`inherit` 是增量 1 的既有行为，无物化、无本篇状态机。`worktree` 触发本篇的全部契约。其它
取值一律拒绝；调用面不接受 `workDir`、`workspacePath`、repository 名、base branch、revision
或任意文件系统路径。

### ChildSessionId 语法与确定性推导

`ChildSessionId` 是 child AgentSession 的稳定身份，由 Server 用系统 ID 语法分配（当前为
`Guid` 的 32 位小写十六进制 "N" 格式）。Runner 校验 `ChildSessionId` 命中系统 ID 语法
（`^[0-9a-f]{32}$`），拒绝任何不合规则的 ID——因此路径、分支与 worktree 名**无需 sanitize**，
直接用原始 `ChildSessionId`。

Runner 对一个 `ChildSessionId` 确定性推导出全部本地坐标（这些是 Runner 内部规则，对 Server
不透明，Server 不构造它们）：

```text
WorktreePath(ChildSessionId)   = <runnerRoot>/agent-workspaces/<ChildSessionId>
WorktreeBranch(ChildSessionId) = mohist/wt-<ChildSessionId>
WorktreeName(ChildSessionId)   = <ChildSessionId>            # git worktree 名
WorkspaceIdentity(ChildSessionId) = agent-wt:<ChildSessionId> # 返回给 Server 的 opaque 句柄
```

因为四者都由 `ChildSessionId` 单值推导，registry 丢失后 Runner 能凭 `ChildSessionId` 重新
定位 path、重建 identity、并在磁盘上重新校验 worktree（见「Recover」），无需 marker，也不污染
用户的 working tree。

### Managed worktree 配置与来源

```text
ManagedWorkspace
  ChildSessionId        物化键；1:1 对应一个 worktree（workDir 不可变）
  ParentSessionId       parent AgentSession（其 authoritative workDir 是 worktree 的 git 来源）
  Repository            Project Repository 快照 { name, gitUrl, baseBranch }（Server 解析后交给 Runner）
  BaseRevision          parent 工作空间当前已提交的 HEAD（Runner 在物化时本地读取）
```

- **来源 = Project Repository（confirmed）**：`Repository` 快照来自 parent 的 **confirmed**
  `WorkspaceRepository`（见下「WorkspaceRepository」），不是 child 选择，也不是任意 ParentWorkDir。
  Runner 用它校验 parent workDir 的 `origin` 与来源一致（见「物化契约」）。
- **base = parent committed HEAD**：worktree 从 parent 工作空间物化时刻的已提交 HEAD 创建，使
  child 能在 parent 当前进度上继续工作。未提交的 working tree 改动不进入 worktree。物化是单次
  同步操作：Runner 在同一调用内读 HEAD 并创建 worktree 分支，HEAD 读写之间无竞态，无需另设
  revision 锁。base 与来源一致由「parent workDir 的 `origin` == Repository.gitUrl」保证：parent
  HEAD 必然是 Project Repository 的一个提交。

### WorkspaceRepository（来源生产者与确认）

worktree mode 要求 parent 拥有 **confirmed** 的 `WorkspaceRepository`——即 parent 工作空间被证明
是某个 Project Repository 的受管工作空间。「由 Project Space 定义」由这条 confirmed 来源链落实，
不是任意 ParentWorkDir。

`WorkspaceRepository` 是 AgentSession authoritative 工作空间上下文的扩展字段（在
[`agent-execution.md`](agent-execution.md) 的不可变 `WorkDir` 之外）：

```text
WorkspaceRepository
  Name               Project Repository 资源名（不可变）
  GitUrl             解析快照（不可变）
  BaseBranch         解析快照（不可变）
  State              unconfirmed | confirmed | rejected
  RejectionReason?   origin-mismatch | not-runner-owned
```

#### 生产者（确认路径）

| parent 来源 | WorkspaceRepository | worktree parent |
|---|---|---|
| **explicit Project-backed launch**：direct launch 同时提供 `context.repository`（Project Repository 名）与 `context.workspacePath` | launch 时 `State=unconfirmed`（Server 解析 `Project.Repository(name)` 得 `{gitUrl,baseBranch}` 快照，workDir=workspacePath）；Runner 首轮执行校验 origin/runner-owned 成功后回报，Server 置 `State=confirmed`（失败置 `rejected`） | `confirmed` 允许；`unconfirmed` 拒绝 `workspace_repository_unconfirmed`；`rejected` 拒绝 |
| nested managed-worktree child | 创建时写入 `WorkspaceRepository = plan.Repository`，`State=confirmed`（worktree 由 confirmed parent 物化，来源继承确认） | 允许 |
| direct launch 仅 repository 无 workspacePath、仅 workspacePath 无 repository、或任一未确认 | `unconfirmed`/`null` | 拒绝 |
| Agent Connection / mention / routed launch | 无来源（`null`） | 拒绝 `parent_workspace_not_project_backed` |
| Workflow inline Session | 受现有 source-kind gate 限制（增量 1 接受条件 1：inline Session 不可作 parent），不是当前可达 parent | 不适用 |

`context.workspacePath` 仍是现有 launch 输入，**不**因本契约成为受管 path 权威：Server 把它记录为
workDir（既有行为），但 workDir 是否为 Project-backed 受管工作空间由 Runner 校验决定，不是 Server
读 path 判定。Server 只传 repository 标量与 workDir 给 Runner，不构造/访问 path。

#### Runner 确认回报

Runner 在 parent Session 首轮执行时校验 workDir：

1. workDir 是 **Runner-owned**（见下「Runner-owned 工作空间」）。
2. `git -C <workDir> remote get-url origin` trim== WorkspaceRepository.GitUrl。

成功 → Runner 回报 `workspace_source_confirmed { sessionId, repositoryName }`；Server 置
`State=confirmed`。失败 → 回报 `workspace_source_rejected { sessionId, reason }`
（`origin-mismatch` / `not-runner-owned`）；Server 置 `State=rejected`。确认前（`unconfirmed`）的
Session 可执行自己的工作（workDir 已设），但 spawn worktree mode 以 `workspace_repository_unconfirmed`
拒绝（request fence 保持可重试 observation，待确认推进后同 key 重试验证）。**不允许**把「写了
`context.repository` metadata」当作来源确认。

#### Runner-owned 工作空间

Runner 校验 parent workDir 时，「Runner-owned」的精确定义：路径 resolve 后位于本 Runner
`runnerRoot` 内、任一路径分量非 symlink，且属于以下之一——

- 已注册的 WorkflowRun workspace（在 workflow workspace registry）；或
- 已注册的 agent worktree（在 agent worktree registry）；或
- Runner 显式配置、且位于 root 内的 default workspace。

不满足（越出 root、symlink、未注册的任意目录）一律 `not-runner-owned`，`WorkspaceRepository` 不可
confirmed。该判定完全在 Runner 本地，Server 不读 path。

### Materialization binding 与 launch plan 状态机

```text
WorkspaceBinding                       # 写入 child AgentSession（物化成功后）
  RunnerId                             被 pin 的 Runner（= parent binding 的 RunnerId）
  WorkspaceIdentity                    Runner 签发的 opaque 句柄（= agent-wt:<ChildSessionId>）
  WorkDir                              Runner 返回的物化路径；即 child AgentSession.workDir
```

物化进度作为 **durable 字段**持久化在 spawn launch plan 上（由 coordinator 持久化，独立于 child
Session grain 的创建）。因此 recovery 与 abort 能在 child Session 尚未创建时就读取并重放它们：

```text
SpawnLaunchPlan（worktree-mode 扩展）
  WorkspaceMode                inherit | worktree（不可变，属于 request fingerprint）
  MaterializeState             none | requested | materialized | rejected
  WorkspaceIdentity?           materialized 时写入
  MaterializedWorkDir?         materialized 时写入（成为 child workDir）
  MaterializeRejectionReason?  rejected 时写入
  ReleaseState                 none | pending | released
```

`WorkspaceBinding` 与 [`RuntimeBinding`](agent-execution.md#当前-runtime-binding)（RunnerId +
runtime + runtimeSessionId）是两条并列的绑定事实：`WorkDir` 不可变，`RuntimeBinding` 可替换；
两者都指向同一个被 pin 的 Runner。

## Semantics

### Spawn 工作空间意图

CLI：

```bash
mo agent spawn <agent-ref> --project <project-id> --parent-session <session-id> \
  --prompt "<brief>" --idempotency-key <key> [--workspace inherit|worktree]
```

`--workspace` 省略时为 `inherit`。Server 调用面：

```text
POST /api/projects/{projectRef}/agent-sessions/{parentSessionId}/spawns
Idempotency-Key: {key}
{ "targetAgentRef": "reviewer", "prompt": "...", "workspace": "worktree" }
```

`workspace` 进入 spawn 请求指纹：`SpawnRequestFence.RequestFingerprint` 固定
`(targetAgentRef, prompt, workspaceMode)`。同 key 同 mode 重放收敛；同 key 异 mode 返回 HTTP 409
idempotency conflict。body 不接受 `workDir`、`workspacePath` 或任意路径。

### 接受条件（worktree mode）

worktree mode 在增量 1 的全部接受条件之外，额外要求（均为 Server 侧 pre-plan acceptance，不
触碰文件系统）：

1. 增量 1 第 5、6 条已确认 parent 有 authoritative workDir 与当前可用的 Runtime binding，parent
   binding 的 Runner 在线。
2. parent 的 `WorkspaceRepository` 存在且 `State == confirmed`（即 parent 是 Project-backed
   且已经 Runner 首轮校验确认）。未确认（`unconfirmed`）时为可重试 observation
   `workspace_repository_unconfirmed`（request fence 保持可重试，待确认推进后同 key 重试验证）；
   `rejected`/不存在时 terminal pre-plan rejection `parent_workspace_not_project_backed`。
3. Server 能从该 confirmed `WorkspaceRepository` 取出 `{ name, gitUrl, baseBranch }` 快照交给 Runner。
   快照缺失时 terminal pre-plan rejection `workspace_repository_unresolved`。

`inherit` mode 不改变增量 1 的任何接受条件；它不物化，Server 直接用 parent authoritative
workDir 作为 child workDir。parent workDir 是否真为可物化的 git 工作空间、其 `origin` 是否与
Project Repository 一致，属于物化期 Runner 校验（见下），不是 Server pre-plan acceptance。

### 物化契约（Server → 被 pin 的 Runner）

物化与释放是 Runner 侧 durable 原语，经既有 Runner 命令通道调用（与 Session command / workspace
查询同一 RPC 通道），不是工作 dispatch，不进 poll/pull 调度通道。Runner 把 agent worktree 状态
写入本地 durable 注册表（write-through、原子写、fail-open 可重建），注册表条目即物化/释放的
幂等记录——不需要第二份 operation log。

#### Request identity 与 idempotency

```text
MaterializeAgentWorkspace(request)
  ProjectId          来源 Project（与 ChildSessionId 共同定位 activity 查询）
  ChildSessionId     稳定物化键（系统 ID 语法）
  ParentWorkDir      parent authoritative workDir（被 pin Runner 上的权威路径）
  Repository         { name, gitUrl, baseBranch }  Server 从 confirmed parent WorkspaceRepository 取出的快照

  -> Materialized { WorkspaceIdentity, WorkDir }
  |  Rejected(reason)
       capacity              Runner 本地工作空间 storage budget 已满（与 workflow workspace 共用同一 budget）
       permission            Runner 无权在该 root 下创建目录
       parent-workspace-unavailable   ParentWorkDir 不属于本 Runner、无 .git 或不可读 HEAD
       repository-mismatch   ParentWorkDir 的 origin != Repository.gitUrl（parent 不是该 Project Repository）
       invalid               ChildSessionId 不合语法，或 path/branch 校验失败
  |  Unknown                 超时 / 连接丢失 / 结果无法确认
```

- **幂等键 = `ChildSessionId`**：同一 `ChildSessionId` 的重复请求返回同一个
  `(WorkspaceIdentity, WorkDir)`，不重建第二个 worktree。
- **opaque identity**：`WorkspaceIdentity = agent-wt:<ChildSessionId>`（Runner 内部规则）。Server
  视为不透明句柄，仅用于 release/校验；Server 不从 `ChildSessionId`、path 或 environment 反推它，
  也不把它当作第二套实体身份。
- **path 不可由 Server 构造**：`WorkDir` 是 Runner 返回的字符串，Server 持久化它但从不构造、
  校验或读写它指向的目录。

#### 物化期 Runner 校验与本地顺序

Runner 收到 `MaterializeAgentWorkspace` 后的本地顺序（每一步 fail-closed，不静默修复）：

```text
require ChildSessionId matches ^[0-9a-f]{32}$
require ParentWorkDir resolves under <runnerRoot>, no path component is a symlink
require ParentWorkDir is a workspace this runner owns, with a readable .git
require (git -C <ParentWorkDir> remote get-url origin) trim== Repository.gitUrl   # else repository-mismatch
path   = WorktreePath(ChildSessionId)
branch = WorktreeBranch(ChildSessionId)
if exists(path):
    require path under <runnerRoot>/agent-workspaces/, no symlink
    require <path>/.git is a worktree file whose backing entry name == WorktreeName(ChildSessionId)
    require current branch == branch
    require backing parent resolves to a workspace this runner owns
    re-register { ChildSessionId, identity, path, branch, ParentWorkDir, Repository.name, phase=active }   # adoption
else:
    head = git -C <ParentWorkDir> rev-parse HEAD                  # parent committed HEAD
    git -C <ParentWorkDir> worktree add -B <branch> <path> <head>
    register { ... phase=active }
return { WorkspaceIdentity = agent-wt:<ChildSessionId>, WorkDir = path }
```

worktree 是 parent 工作空间仓库的一个 linked working tree，共享对象库。任何校验失败都返回
`Rejected(invalid | repository-mismatch | parent-workspace-unavailable)`，**不修改**已存在目录。
`inherit` mode 不调用此原语。

### 物化状态机与恢复

`MaterializeState` 转移（持久化在 launch plan 上）：

```text
none      --(worktree mode, send MaterializeCommand)-->  requested
requested --(Runner Materialized)-->                    materialized   # 记录 identity + workDir
requested --(Runner Rejected)-->                        rejected        # 记录 reason（durable terminal）
requested --(Runner Unknown)-->                         requested       # 可恢复，停留
```

- **物化时机**：在 canonical launch pipeline 中「persist launch plan + reserve EdgeId」之后、
  「prepare child AgentJob」与「create child AgentSession」之前完成（见
  [`subagents.md`](subagents.md) 的 launch pipeline）。child workDir 在 Session 创建前就已由物化
  确定。
- **Unknown 是 admitted plan 内的可恢复状态**，**不是** SpawnRequestFence 回到 `validation-pending`：
  plan 已 admitted（immutable），`MaterializeState=requested` 是 plan 内的瞬态。外部观察到的是
  retryable observation：HTTP 202 Accepted + 稳定 code `materialization-in-progress`（abort in-flight
  时为 `spawn-abort-in-progress`）；同 key replay 重发同一 stable `MaterializeCommand`，Runner 幂等
  返回既有结果或现在物化，收敛到 `materialized` 或 `rejected`。措辞统一为「plan 已 admitted、物化
  （或 abort）尚未确认 terminal」，不使用「已 admitted / 物化未确认」的含混表述。
- **恢复窗口**（coordinator activation loss / reminder 重放，逐个读 `MaterializeState`）：
  `none`(worktree) → 发 MaterializeCommand；`requested` → 重发同一 command；`materialized` → 用记录的
  workDir 继续 pipeline（创建 Session）；`rejected` → 重放 durable 拒绝。
- 与既有规则兼容：plan immutable；`rejected` 是 durable post-plan terminal（同 key 重放固定结果）；
  `requested` 是其内的可重放瞬态，不创建 child artifact、不改 fingerprint。

### Release

```text
ReleaseAgentWorkspace(request)
  ChildSessionId       幂等键
  WorkspaceIdentity    校验句柄

  -> Released        已标记 eligible 或已删除（含 NotFound）
  |  NotFound        该 identity 在本 Runner 无登记
  |  Unknown         结果无法确认
```

请求**同时**携带 `ChildSessionId`（幂等键）与 `WorkspaceIdentity`（校验句柄）。Runner 校验
`WorkspaceIdentity == agent-wt:<ChildSessionId>`，不匹配一律拒绝（防错放）。两者是 canonical
identity 对，不再有「或等价」。

Release 由 **abort** 触发（物化已发生但 plan 被拒绝）。abort 是一条稳定 command，按下面顺序
收敛，任一步中断后重放同一 abort command 完成剩余：

```text
1. 把 MaterializeState 收敛到 terminal：
     requested -> 重发 MaterializeCommand 取得 materialized | rejected
     (materialized 进入下一步；rejected 无需 release)
2. 若 materialized：发 ReleaseAgentWorkspace -> ReleaseState pending -> released
     release Unknown 时保持 pending，由 Runner 回收周期兜底
3. 收敛 provisional artifacts（reservation rejected、Job cancelled、initial Turn cancelled、
     Session idle、无 visible link/input/callback）
```

这处理「物化 late + abort」竞态：abort 先把物化收敛到确定结果，再按结果决定是否 release。

### 生命周期

| 阶段 | 含义 |
|---|---|
| `active` | worktree 已物化且其 child AgentSession 仍可能被使用；Runner 禁止自动回收 |
| `eligible` | 满足下方 eligible 条件之一；Runner 可按 storage budget 回收 |
| `stuck` | 回收安全检查确定性拒绝；保留目录与登记，不再自动重试 |

**eligible 条件**（任一满足；`active`/`idle-within-retention`/`pending`/`unknown` 永不 eligible）：

1. **显式 release**：`ReleaseAgentWorkspace` 被 Runner 确认（Server 对自己的 abort 决定是权威）。
2. **idle 超过保留阈值**：Server 权威 `activity=idle` 且 `IdleSince` 早于保留阈值。
3. **orphan 确认**：Server 对该 `ChildSessionId` 返回 `not-found`，且在至少一次后续维护周期
   recheck 仍为 `not-found`（见下「orphan」）。

- **创建**：worktree mode spawn 在 launch pipeline 的物化步骤完成。
- **复用**：child Session 的后续 Turn、follow-up、Compact、Reset 复用同一 workDir（不可变），不
  重新物化。
- **隔离**：worktree 在独立路径、独立 `mohist/wt-<ChildSessionId>` 分支上；与 parent 工作树物理
  隔离，文件冲突由 parent 分单纪律避免，git 是最终仲裁。
- **清理/保留**：Runner 的 workspace 维护周期处理 agent worktree，与 workflow workspace 共用周期、
  single-flight 约束与本地 **storage budget**。删除 worktree 必须用 `git worktree remove`（清除工作树
  与 parent 仓库的 admin entry/branch），辅以 `git worktree prune` 清理已无目录的 stale admin entry；
  **禁止** `rm -rf` 留下 parent 仓库的 stale `.git/worktrees/<name>` entry。eligible 判定权在 Server
  （activity 权威）：Runner 周期性按 `(ProjectId, ChildSessionId)` 向 Server 查询本地仍 `active` 的
  agent worktree 所属 Session 的 activity，镜像 workflow workspace 的 status 收敛。保留阈值与 storage
  budget 是 Runner 运行参数，不是领域概念。
- **detach**：child 从树摘下成为新 root；workDir 不变、worktree 不删、Source 不改。detached
  child 的 worktree 照常按 idle/保留回收。
- **stop / cascade stop**：停止只结束当前 Turn 的执行，不删 worktree。stop snapshot 内的 child
  按 [`subagents.md`](subagents.md) 的 cascade target 语义处理其 Turn；worktree 保持 `active`
  直到满足 eligible 条件。
- **terminal**：AgentSession 永不进入 terminal。child launch 的 `ChildLaunchJobId` 终态触发
  terminal report，但**不**单独让 worktree 立即 eligible——worktree 的 eligible 仍由 idle/保留或
  orphan 决定，保证 Job 终态后的 follow-up 仍能复用 workDir。

#### Activity 查询与 IdleSince

Server 是 activity 权威，持有 durable `IdleSince`：Session 上次转入 `idle`（无 active/queued Turn）
的时间戳，由注入的 `TimeProvider` 写入；`activity` 转回 `active` 时清空。Runner 周期查询本地
`active` agent worktree 所属 Session 的 activity，按 `(ProjectId, ChildSessionId)` 寻址（registry
条目携带 `ProjectId`）。Server 返回确定形状：

```text
{ state: active }                 有 active/queued Turn；永不 eligible
{ state: idle, idleSince }        idle；idleSince 与保留阈值比较
{ state: pending }                存在未终态的 materialization/launch plan（spawn 仍在进行）；永不 eligible
{ state: not-found }              该 (ProjectId, ChildSessionId) 无 Session（orphan 候选）
{ state: unknown }                activity 不可确认；永不 eligible，下次再查
```

`pending`/`unknown` 永不 eligible（fail-closed）：`pending` 表示该 worktree 所属 spawn 尚未收敛，
回收会破坏进行中的物化/launch。`IdleSince` 的语义由本文固定，实现者不得自行选择替代字段或推断。

#### orphan 与 grace

`not-found` 表示 Server 无该 `ChildSessionId` 的 Session 记录，发生在：spawn abort 于 child
Session 创建前（物化已发生）、late 物化、或 Session 从未 finalize。为防提前回收 active child 的
未提交改动，`not-found` **不立即** eligible：

- `not-found` → orphan 候选；Runner 在下一次维护周期 recheck。
- 连续两次（或配置的 recheck 次数）`not-found` 才 → `eligible`。任一次 recheck 返回
  `active`/`idle`/`pending`/`unknown` 即撤销 orphan 候选、回到正常判定。
- 删除前仍须取得该 directory 的 removal fence（与 workflow workspace 同一机制），确认无活跃
  Runtime 操作占用目录；fence 失败则 `stuck`。

显式 release（条件 1）不经 grace：Server 对 abort 是权威，release 确认即可 eligible。

#### capacity

`Rejected(capacity)` 指 Runner 本地**工作空间 storage budget** 已满——与 workflow workspace
retention 共用的同一磁盘 budget，**不是**并发 slot（slot 约束工作 dispatch，与物化无关）。Runner
在物化前检查新增 worktree 是否超出 budget；超出即 `capacity` 拒绝，spawn 走 post-plan rejection。

### Recover（Runner 重启 / registry 丢失 / worktree 丢失）

git worktree 是磁盘文件，Runner 重启后存续；Runner 注册表是 fail-open 索引（可从磁盘重建）。
Runner 启动与每次物化/释放都凭 `ChildSessionId` 的确定性推导自愈：

| 磁盘事实 | Runner 行为 |
|---|---|
| path 存在且校验通过（`.git` worktree 文件 → 期望 backing entry、branch、containment、非 symlink、parent 属本 Runner） | adoption / re-register，phase 保持 |
| path 存在但校验不通过（branch/parent/identity 不符、symlink、越出 root） | `Rejected(invalid)`；不修改目录；若属已 bound worktree 则 child 访问以 `workspace-corrupt` 失败 |
| path 不存在 | 视为未物化；若 child 仍 bound，下次访问以 `workspace-missing` 失败 |
| parent 仓库被重建/丢失，worktree `.git` backing 失效 | worktree 无效；child AgentJob 以 `workspace-corrupt` 失败 |

- **无 marker**：identity 由 git worktree 自身元数据（`.git` 文件 → parent 的 worktree 列表条目）
  + 确定性 `WorktreeName(ChildSessionId)` 表达，不向用户 working tree 写 marker。
- **registry 丢失重扫**：Runner 启动或 registry 不可读时，确定性重扫 `<runnerRoot>/agent-workspaces/`
  下命中安全 ID 语法（`^[0-9a-f]{32}$`）的目录，对每个目录读 `.git` worktree 文件 → parent 仓库、
  校验 branch/containment/非 symlink 后 adoption/re-register，并重建 parent-child 工作空间依赖
  index。这是确定性 path/name 推导 + git 元数据校验，**不是猜身份**：目录名即 `ChildSessionId`、
  parent 由 `.git` backing 读出，任何校验不通过的目录跳过（不当作 active parent 误删的依据）。
- **Server 不重建 worktree**：workDir 不可变，Server 不在另一个 Runner 上重新物化。丢失的 child
  worktree 让 child AgentJob 失败，parent 经 terminal report 自行决定是否重新 spawn——与
  WorkflowRun workspace「损坏或 root 丢失后由 Workflow 重新执行」对称。
- **不跨 Runner 迁移**：物化绑定在被 pin 的 Runner。该 Runner 永久消失时 child workDir 失效，
  child AgentJob 失败；不存在「把 worktree 搬到另一个 Runner」的恢复路径。

#### parent-child 工作空间依赖

child worktree 是 parent 工作空间仓库的 linked working tree，依赖 parent 工作空间在本 Runner
存在。Runner 的 workspace 维护在判定 parent 工作空间 eligible/删除前，必须先检查本地是否存在
指向它的 `active` child worktree：

| parent 清理候选时 | Runner 行为 |
|---|---|
| 存在 `active` child worktree 指向该 parent | 推迟 parent 清理，直到所有 child worktree 满足 eligible 或被 release |
| 无 active child worktree | 按既有 workflow workspace 规则清理 parent |

该依赖完全在 Runner 本地（parent 与 child 在同一被 pin Runner），无需跨 Runner 协调，也无需
Server 维护父子工作空间拓扑。

## Failure semantics 汇总

| 失败 | 结果 |
|---|---|
| spawn body 出现非 `inherit`/`worktree` 的 workspace 值 | terminal pre-plan rejection `invalid-workspace-mode`；不创建 child |
| 同 key 异 mode 重放 | HTTP 409 idempotency conflict |
| parent 无 Project 来源（`WorkspaceRepository` 为 `null`/`rejected`） | terminal pre-plan rejection `parent_workspace_not_project_backed` |
| parent `WorkspaceRepository` 未确认（`unconfirmed`） | 可重试 observation `workspace_repository_unconfirmed`；request fence 保持可重试，确认推进后同 key 重试验证 |
| confirmed `WorkspaceRepository` 快照缺失 | terminal pre-plan rejection `workspace_repository_unresolved` |
| 物化 `Rejected(capacity)` | post-plan rejection；同 key 重放同一拒绝 |
| 物化 `Rejected(repository-mismatch)` | post-plan rejection；parent workDir 的 origin 与 Project Repository 不一致 |
| 物化 `Rejected(parent-workspace-unavailable \| permission \| invalid)` | post-plan rejection；同 key 重放同一拒绝 |
| 物化 `Unknown` | `MaterializeState=requested`（admitted plan 内可恢复）；同 key 重发收敛，不猜 path、不回退 inherit、不回 validation-pending |
| 物化后 abort | abort command 先收敛物化到 terminal，再 `ReleaseAgentWorkspace`；release 未知则 pending，回收周期兜底 |
| worktree 磁盘损坏/丢失 | child AgentJob `workspace-corrupt`/`workspace-missing` 失败；parent 收 terminal report |
| 被 pin Runner 永久消失 | child workDir 失效，child AgentJob 失败；不跨 Runner 迁移 |
| parent 工作空间被重建 | 指向它的 child worktree 失效；按上条处理 |
| Runner activity 查询返回 `unknown`/`pending` | worktree 永不 eligible；下次再查 |
| `not-found` 未经 grace recheck | 不 eligible；连续 recheck 确认后才 eligible |

## 验证

Server spec 必须以 fake Runner、fake clock 与 in-memory stores 覆盖至少：

- workspace mode 白名单：仅 `inherit`/`worktree` 接受，其它 terminal pre-plan reject；同 key 同
  mode 重放、同 key 异 mode 409；省略 mode 等价 `inherit`；
- `inherit` mode 不触发物化，child workDir = parent authoritative workDir（增量 1 行为不变）；
- `worktree` mode Project 来源（真实生产路径）：explicit Project-backed launch
  （`context.repository` + `context.workspacePath`）→ Runner 首轮 origin/runner-owned 校验 →
  `WorkspaceRepository` confirmed → spawn worktree 物化；parent `WorkspaceRepository` 为
  `unconfirmed` 时 `workspace_repository_unconfirmed` 可重试 observation，`null`/`rejected` 时
  `parent_workspace_not_project_backed` terminal pre-plan rejection；nested managed child 继承
  confirmed；无 Project 来源（Agent Connection/mention/未确认 direct launch）fail-closed 拒绝；
  Server 把 confirmed 快照交给 Runner；
- `worktree` mode：Server 向被 pin Runner 发 `MaterializeAgentWorkspace`，child workDir = 返回
  path，child AgentJob pin 到同一 Runner，admission 不改选其它 eligible Runner；
- **durable 状态机**：`MaterializeState` 在 plan 上 none→requested→materialized/rejected 的持久
  化与恢复；`Unknown` 停留 `requested` 且同 key 重发收敛（不回 validation-pending、不猜 path、不
  回退 inherit）；coordinator activation loss 在 child Session 创建前能读 `MaterializeState` 并
  重放；`rejected` 是 durable post-plan terminal；
- **abort 与 release**：物化后 abort 先收敛物化再 release；release 请求同时带 `ChildSessionId` 与
  `WorkspaceIdentity` 且校验一致才生效；release 未知保持 pending；物化 late + abort 时 abort
  command 收敛到确定结果；
- **registry 丢失 / adoption**：Runner 重启或 registry 丢失后，凭 `ChildSessionId` 重新推导
  path/identity 并校验磁盘 worktree（containment、非 symlink、`.git` backing、branch、parent）；
  path 已存在且校验通过则 adoption/re-register，校验不通过则 `Rejected(invalid)` 不修改目录；
  `ChildSessionId` 不合语法被拒；
- 物化 `Rejected(capacity)` 与 storage budget 关系；`repository-mismatch`（parent origin !=
  Repository.gitUrl）；`parent-workspace-unavailable`；
- 生命周期：创建于 spawn、跨 Turn/follow-up 复用同一 workDir、独立路径与分支；detach/stop 不删
  worktree；`ChildLaunchJobId` 终态不立即 eligible；
- **activity / orphan**：Runner 周期查询返回 `active`/`idle+idleSince`/`pending`/`not-found`/`unknown`；
  `pending`/`unknown` 永不 eligible；`idle` 超阈值 eligible；`not-found` 需连续 recheck 才 eligible
  （任一次 recheck 非 not-found 即撤销）；显式 release 不经 grace；删除前 removal fence；删除用
  `git worktree remove`/`prune` 不留 stale entry；
- Server 永不构造/校验/读写 workDir 指向的目录；Runner 永不从环境或目录扫描猜 identity、永不
  为 child 选择不同 Runner。

## 交付拆分

后续实现可拆成独立 issue，每个能用一句话说清独立交付价值：

1. **Runner managed-worktree 物化原语与 WorkspaceSource 确认**：Runner 实现
   `MaterializeAgentWorkspace` / `ReleaseAgentWorkspace`（确定性 identity/path 推导、Project
   Repository origin 校验、adoption、durable 注册表、parent-child 依赖推迟清理、storage budget、
   `git worktree remove`/`prune` 回收、registry 丢失重扫）与 parent workDir 的
   `workspace_source_confirmed`/`rejected` 首轮校验回报。独立价值：Runner 具备物化、来源确认与回收
   隔离 worktree 的能力。
2. **Server WorkspaceRepository 生产者与 spawn 物化状态机**：explicit Project-backed launch 的
   `context.repository`+`context.workspacePath` → `WorkspaceRepository(unconfirmed)`、Runner 确认回报
   → `confirmed`、nested child 继承 `confirmed`；CLI `--workspace`、API body、request fingerprint、
   `workspace_repository_unconfirmed`/`parent_workspace_not_project_backed` 拒绝、launch plan 的
   `MaterializeState`/`ReleaseState` durable 字段与 recovery 收敛、接入 canonical launch pipeline
   （child Session 写入 `WorkspaceRepository = plan.Repository`）。依赖 1。独立价值：
   `mo agent spawn --workspace worktree` 有真实可达 parent 且 crash-safe。
3. **生命周期、activity 查询与 orphan 回收**：Server `IdleSince`、`(ProjectId,ChildSessionId)`
   activity 查询响应（含 `pending`）、Runner 周期 eligible 判定、orphan grace/recheck、removal fence。
   依赖 1、2。独立价值：managed worktree 不无限堆积，按 idle/保留/orphan 安全回收。

## Status

交付增量 4 的范围 A、B 已落地；范围 C 尚未实现。

- **范围 A：Runner 原语与 `WorkspaceRepository` source confirmation（已落地）**。Runner
  提供 managed-worktree 的物化与释放原语，并在 parent 首轮执行时确认工作空间来源，回报
  `workspace_source_confirmed` 或 `workspace_source_rejected`。
- **范围 B：Server producer、spawn durable materialization、CLI/API（已落地）**。explicit
  Project-backed launch 生产 `WorkspaceRepository(unconfirmed)`，Runner 确认后变为
  `confirmed`；nested managed child 继承确认。`--workspace` 与 API 的 `workspace` mode 已
  接入请求指纹和 canonical launch pipeline，`MaterializeState`/`ReleaseState` 及物化后的
  child `WorkspaceRepository` 持久化在 durable launch/session facts 中，物化绑定继续 pin
  parent 的 Runner。
- **范围 C：生命周期回收（当前 gap）**。Server 尚无 durable `IdleSince`，也尚无按
  `(ProjectId, ChildSessionId)` 提供给 Runner 的 activity query；Runner 的 idle/orphan cleanup、
  grace recheck 和 removal fence 尚未实现。因此物化后的 worktree 仍缺少完整的 idle/孤儿安全
  回收路径。

本篇仍是目标 spec；范围 C 的未实现状态不改变上文的物化、释放、生命周期和验证契约。增量 1–3、5
的契约不受影响，仍以 [`subagents.md`](subagents.md) 为权威。
