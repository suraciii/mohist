---
status: wip
---

# Agent 受管工作空间（Managed worktree）

子会话可以请求一个**隔离工作空间**：由 Project Space 定义其来源、由被 pin 的 Runner
物化、由子 AgentSession 拥有工作目录。它是会话树交付增量 4（Managed worktree）的执行
契约，依赖增量 1 的 link/launch 模型与 Runner pinning。

产品行为见 [`../docs/subagents.md`](../docs/subagents.md) 的「实装差距」。会话树的 spawn、
接受条件、父子 link、terminal callback、cascade stop 与 detach 的完整契约以
[`subagents.md`](subagents.md) 为唯一权威；本篇只定义隔离工作空间的资源身份、物化契约与
生命周期，以及它如何接入 spawn。

## 边界与放置

| 关注点 | 归属 | 说明 |
|---|---|---|
| Repository 资源（gitUrl / baseBranch） | Project Space | 唯一定义工作空间的来源仓库；本篇不新增第二个资源类型 |
| 工作空间请求意图（mode） | spawn 调用面 | 只能是受约束的 `inherit` / `worktree`，不是路径 |
| 子 AgentSession.workDir | Session context | 物化成功后固定为物化路径，不可变 |
| 物化权威（worktree 落盘、注册表、回收） | Runner | 唯一接触文件系统者 |
| 物化 binding（哪个 Runner、opaque identity） | Session context | 与 Runtime binding 并列的另一条绑定事实 |
| binding / activity 裁判 | Server | 决定 workDir、解析 mode、签发物化请求；从不读写文件系统 |

- **Server 不碰文件系统**：Server 只持久化 Runner 返回的 path 字符串与 opaque identity，
  从不构造、推导、校验或读写任何工作空间路径。
- **Runner 不猜身份、不换 Runner**：Runner 用请求携带的 child 身份与 opaque identity 寻址
  自己物化的 worktree，不从环境、目录扫描或 Runtime Session 反推身份；物化始终在 parent
  binding 的那个 Runner 上本地完成，child AgentJob 也 pin 到同一个 Runner。

### 与既有工作空间概念的边界

| 概念 | 身份 | 配置 | 生命周期触发 | 物化者 |
|---|---|---|---|---|
| Repository | Project 内资源名 | gitUrl / baseBranch / isDefault | 由未完成 Issue 占用锁定 | 不物化，是来源 |
| WorkflowRun workspace | `WorkflowRunId` 推导 | Issue 目标 Repository | WorkflowRun 终态 | Runner（`<runnerRoot>/workspaces/<workflowRunId>`） |
| AgentSession.workDir | Session 不变事实 | launch 时固定 | Session 无终态 | launch 来源决定 |
| Runtime workspace（如 OpenCode directory Instance） | Runtime 物理资源 | directory = workDir | Runtime 自有 | Runtime adapter |
| **Managed worktree（本篇）** | child AgentSession | Project Repository + parent revision | 见下「生命周期」 | 被 pin 的 Runner（git worktree） |

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

`inherit` 是增量 1 的既有行为，无物化、无本篇契约。`worktree` 触发本篇的全部契约。其它
取值一律拒绝；调用面不接受 `workDir`、`workspacePath`、repository 名、base branch、revision
或任意文件系统路径。

### Managed worktree 身份与配置

```text
ManagedWorkspace
  ChildSessionId        物化键；child AgentSession 的稳定身份，1:1 对应一个 worktree
  ParentSessionId       parent AgentSession（其 authoritative workDir 是 worktree 的来源）
  Repository            Project Repository 的稳定资源名（由 parent 上下文解析，不由 child 选择）
  BaseRevision          parent 工作空间当前已提交的 HEAD（Runner 在本地读取）
  WorktreeBranch        mohist/wt-<sanitized ChildSessionId>
```

- **身份**：worktree 的逻辑身份是 child AgentSession。`workDir` 在 Session 生命周期内不可变，
  因此一个 child Session 至多一个 managed worktree。
- **配置**：repository 与 base revision 都从 parent 的 authoritative 工作空间解析，child 不
  选择仓库或版本。`inherit` 与 `worktree` 的唯一差别是「是否物化隔离工作树」。
- **base = parent committed HEAD**：worktree 从 parent 工作空间当前已提交的 HEAD 创建，使
  child 能在 parent 当前进度上继续工作。未提交的 working tree 改动不进入 worktree。

### Materialization binding

```text
WorkspaceBinding
  RunnerId              被 pin 的 Runner（= parent binding 的 RunnerId）
  WorkspaceIdentity     Runner 签发的 opaque 句柄；release/recover/校验用它，不用 childSessionId 反推
  WorkDir               Runner 返回的物化路径；即 child AgentSession.workDir
```

`WorkspaceBinding` 与 [`RuntimeBinding`](agent-execution.md#当前-runtime-binding)（RunnerId +
runtime + runtimeSessionId）是两条并列的绑定事实：

- `WorkspaceBinding.WorkDir` 是不可变的工作目录；`RuntimeBinding` 是可替换的物理 Session。
- 两者都指向同一个被 pin 的 Runner：worktree 物化要求 parent Runner 在场，child AgentJob 也
  pin 到该 Runner（增量 1 第 6 条）。
- `RuntimeBinding` 的缺失恢复 / Reset 不改变 `WorkDir`；`WorkspaceBinding` 的丢失见「Recover」。

## Semantics

### Spawn 工作空间意图

CLI 规范命令（在增量 1 的 spawn 上增加一个受约束开关）：

```bash
mo agent spawn <agent-ref> --project <project-id> --parent-session <session-id> \
  --prompt "<brief>" --idempotency-key <key> [--workspace inherit|worktree]
```

`--workspace` 省略时为 `inherit`。CLI 不得从工作目录、Runtime Session 或环境猜 mode。

Server 规范调用面：

```text
POST /api/projects/{projectRef}/agent-sessions/{parentSessionId}/spawns
Idempotency-Key: {key}

{ "targetAgentRef": "reviewer", "prompt": "...", "workspace": "worktree" }
```

`workspace` 可选，默认 `inherit`。body 仍不接受 `workDir`、`workspacePath`、Runner、Runtime 或
任意路径。

`workspace` 进入 spawn 请求指纹：`SpawnRequestFence.RequestFingerprint` 固定
`(targetAgentRef, prompt, workspaceMode)`。同 key 同 mode 重放收敛；同 key 异 mode 返回 HTTP 409
idempotency conflict。

### 接受条件（worktree mode）

`worktree` mode 在增量 1 的全部接受条件之外，额外要求：

1. 增量 1 第 5、6 条已确认 parent 有 authoritative workDir 与当前可用的 Runtime binding；parent
   binding 的 Runner 在线。这是物化的前提——parent 工作空间必须存在于被 pin 的 Runner 本地。
2. parent 的 authoritative workDir 是该 Runner 上一个可物化的工作空间（有 `.git`、可读
   HEAD）。Server 把 parent workDir 作为权威事实交给 Runner 校验，不自行判断磁盘状态。
3. 物化请求对被 pin 的 Runner 发起后，必须得到确定结果（见「物化契约」），才能进入后续
   launch pipeline。

`inherit` mode 不改变增量 1 的任何接受条件；它不物化，Server 直接用 parent authoritative
workDir 作为 child workDir。

### 物化契约（Server → 被 pin 的 Runner）

物化与释放是 Runner 侧原语，经既有 Runner 命令通道（与 Session command / workspace 查询同一
SignalR 通道）调用，不是工作 dispatch。它们不进入 poll/pull 调度通道，也不创建第二条 dispatch
管道。

#### Request identity 与 idempotency

```text
MaterializeAgentWorkspace(request)
  ChildSessionId     稳定物化键
  ParentWorkDir      parent authoritative workDir（被 pin Runner 上的权威路径）

  -> Materialized
       WorkspaceIdentity   Runner 签发的 opaque 句柄
       WorkDir             物化路径（写入 child AgentSession.workDir）
  |  Rejected(reason)      capacity | permission | parent-workspace-unavailable | invalid
  |  Unknown                超时 / 连接丢失 / 结果无法确认
```

- **idempotency**：物化键是 `ChildSessionId`。Runner 用它在自己本地注册表里登记 worktree；
  同一 `ChildSessionId` 的重复请求返回同一个 `(WorkspaceIdentity, WorkDir)`，不重建第二个
  worktree。
- **opaque identity**：`WorkspaceIdentity` 由 Runner 签发，Server 视为不透明句柄，仅用于后续
  release/recover/校验。Server 不从 `ChildSessionId`、path 或 environment 反推它，也不把它
  当作第二套实体身份。
- **path 不可由 Server 构造**：`WorkDir` 是 Runner 返回的字符串，Server 持久化它但从不构造、
  校验或读写它指向的目录。

Runner 收到 `MaterializeAgentWorkspace` 后的本地顺序：

```text
validate ParentWorkDir is a workspace this runner owns and that has a readable .git
head = git -C <ParentWorkDir> rev-parse HEAD          # parent committed HEAD
path   = <runnerRoot>/agent-workspaces/<sanitized ChildSessionId>
branch = mohist/wt-<sanitized ChildSessionId>
git -C <ParentWorkDir> worktree add -B <branch> <path> <head>
register { ChildSessionId, WorkspaceIdentity, path, branch, ParentWorkDir, phase=active }
return { WorkspaceIdentity, WorkDir=path }
```

worktree 是 parent 工作空间仓库的一个 linked working tree，共享对象库。`inherit` mode 不调用
此原语。

#### Fail-closed 矩阵

Server 必须在以下每种情况 fail-closed，绝不猜测 path、绝不换 Runner、绝不让 child 在未确认
物化的 workDir 上 dispatch：

| 情形 | Server 行为 |
|---|---|
| 物化确定成功 | 记录 `WorkDir` + `WorkspaceBinding`，进入 launch pipeline |
| 物化 `Rejected`（capacity / permission / parent-workspace-unavailable / invalid） | post-plan 拒绝：Job cancelled、initial Turn cancelled、Session idle、无 link/input/callback；同 key 重放同一拒绝 |
| 物化 `Unknown`（超时 / 连接丢失 / 结果无法确认） | **不猜 workDir**；保持 request fence 为可重试 observation，同 key 重发同一物化请求；Runner 若已物化则返回既有结果，若未物化则现在物化。绝不创建空 workDir 或回退 `inherit` |
| 重复请求（同 `ChildSessionId`） | Runner 返回既有 `(WorkspaceIdentity, WorkDir)`；幂等 |
| Runner 在物化后、dispatch 前离线 | child AgentJob pin 到该 Runner，按普通 dispatch 语义等待 Runner 回归；worktree 已落盘，Runner 回归后注册表重新校验并复用 |
| 停止竞态：物化已成功但 spawn 被 abort（reservation 被 stop snapshot 拒绝 / parent reset / binding 变更） | Server 以稳定 abort command 向 Runner 发 `ReleaseAgentWorkspace(WorkspaceIdentity)`；release 结果未知时保留为 pending，由 Runner 回收周期兜底，绝不让孤儿 worktree 被静默当作已绑定的 child workDir |
| 物化前 spawn 被 abort | 不发 release（物化未确定发生）；若有 late 物化，Runner 在 child Session 始终无 binding 时由回收周期识别为孤儿 |

### Release 与 Recover

```text
ReleaseAgentWorkspace(request)
  WorkspaceIdentity   （或等价的 ChildSessionId）

  -> Released        已标记 eligible 或已删除
  |  NotFound        该 identity 在本 Runner 无登记（可能从未物化或已回收）
  |  Unknown         结果无法确认
```

#### 生命周期

| 阶段 | 含义 |
|---|---|
| `active` | worktree 已物化且其 child AgentSession 仍可能被使用；Runner 禁止自动回收 |
| `eligible` | Server 已确认 child AgentSession 长时间 idle（无 active/queued Turn，超过保留阈值），或显式 release；Runner 可按 storage budget 回收 |
| `stuck` | 回收安全检查确定性拒绝；保留目录与登记，不再自动重试 |

- **创建**：`worktree` mode spawn 被接受后，在 launch pipeline 中「预留 child 身份 → 物化 → 创建
  child AgentSession(workDir=物化路径)」之间完成。
- **复用**：child Session 的后续 Turn、follow-up、Compact、Reset 复用同一 workDir（不可变），
  不重新物化。
- **隔离**：worktree 在独立路径、独立 `mohist/wt-<childSession>` 分支上；与 parent 工作树物理
  隔离，文件冲突由 parent 分单纪律避免，git 是最终仲裁。
- **清理/保留**：Runner 的 workspace 维护周期处理 agent worktree，与 workflow workspace 共用
  周期与 single-flight 约束。eligible 判定权在 Server（activity 权威）：Runner 周期性向 Server
  查询本地仍 `active` 的 agent worktree 所属 Session 的 activity，镜像 workflow workspace 的
  status 收敛；idle 超过保留阈值的标记 `eligible`。保留阈值是 Runner 运行参数，不是领域概念。
- **detach**：child 从树摘下成为新 root；workDir 不变、worktree 不删、Source 不改。detached
  child 的 worktree 照常按 idle/保留回收。
- **stop / cascade stop**：停止只结束当前 Turn 的执行，不删 worktree。stop snapshot 内的 child
  按 [`subagents.md`](subagents.md) 的 cascade target 语义处理其 Turn；worktree 保持 `active`
  直到 idle 超过保留阈值。
- **terminal**：AgentSession 永不进入 terminal。最接近的终态信号是 child launch 的
  `ChildLaunchJobId` 进入终态——它触发 terminal report，但**不**单独让 worktree 立即 eligible；
  worktree 的 eligible 仍由 Session idle/保留决定，保证 Job 终态后的 follow-up 仍能复用 workDir。

#### Recover（Runner 重启 / worktree 丢失）

git worktree 是磁盘文件，Runner 重启后存续。Runner 启动时重读本地注册表并重新校验 worktree：

| 磁盘事实 | Runner 行为 |
|---|---|
| worktree 完好（`.git` link 有效、parent 仓库在位） | 复用，phase 保持 |
| parent 仓库已被重建/丢失，worktree `.git` link 失效 | worktree 无效；child AgentJob 下次访问以 `workspace-corrupt` 失败；Server 不重建、不换 Runner |
| worktree 目录消失 | 视为未物化；若 child 仍 bound，下次访问以 `workspace-missing` 失败 |

- **Server 不重建 worktree**：workDir 不可变，Server 不在另一个 Runner 上重新物化。这与
  WorkflowRun workspace「损坏或 root 丢失后由 Workflow 重新执行」对称——丢失的 child worktree
  让 child AgentJob 失败，parent 经 terminal report 自行决定是否重新 spawn。
- **不跨 Runner 迁移**：物化绑定在被 pin 的 Runner。该 Runner 永久消失时，child workDir 失效，
  child AgentJob 失败；不存在「把 worktree 搬到另一个 Runner」的恢复路径。

#### parent-child 工作空间依赖

child worktree 是 parent 工作空间仓库的 linked working tree，依赖 parent 工作空间在本 Runner
存在。Runner 的 workspace 维护在判定 parent 工作空间 eligible/删除前，必须先检查本地是否存在
指向它的 `active` child worktree：

| parent 清理候选时 | Runner 行为 |
|---|---|
| 存在 `active` child worktree 指向该 parent | 推迟 parent 清理，直到所有 child worktree 进入 `eligible`/`stuck` 或被 release |
| 无 active child worktree | 按既有 workflow workspace 规则清理 parent |

该依赖完全在 Runner 本地（parent 与 child 在同一被 pin Runner），无需跨 Runner 协调，也无需
Server 维护父子工作空间拓扑。

## Failure semantics 汇总

| 失败 | 结果 |
|---|---|
| spawn body 出现非 `inherit`/`worktree` 的 workspace 值 | terminal pre-plan rejection `invalid-workspace-mode`；不创建 child |
| 同 key 异 mode 重放 | HTTP 409 idempotency conflict |
| 物化 `Rejected`（capacity/permission/parent 不可物化） | post-plan rejection，收敛 provisional artifacts，同 key 重放同一拒绝 |
| 物化 `Unknown` | request fence 保持可重试 observation，同 key 重发；不猜 path、不回退 inherit |
| 物化后 abort | `ReleaseAgentWorkspace`；release 未知则 pending，回收周期兜底 |
| worktree 磁盘损坏/丢失 | child AgentJob `workspace-corrupt`/`workspace-missing` 失败；parent 收 terminal report |
| 被 pin Runner 永久消失 | child workDir 失效，child AgentJob 失败；不跨 Runner 迁移 |
| parent 工作空间被重建 | 指向它的 child worktree 失效；按上条处理 |

## 验证

Server spec 必须以 fake Runner、fake clock 与 in-memory stores 覆盖至少：

- workspace mode 白名单：仅 `inherit`/`worktree` 接受，其它 terminal pre-plan reject；同 key
  同 mode 重放、同 key 异 mode 409；省略 mode 等价 `inherit`；
- `inherit` mode 不触发物化，child workDir = parent authoritative workDir（增量 1 行为不变）；
- `worktree` mode：Server 向被 pin Runner 发 `MaterializeAgentWorkspace`，child workDir = 返回
  path，child AgentJob pin 到同一 Runner，且 admission 不会改选其它 eligible Runner；
- 物化 idempotency：同 `ChildSessionId` 重复请求返回同一 `(identity, path)`；
- 物化 `Unknown`：Server 不写 workDir、不 dispatch、不回退 `inherit`；同 key 重发后 Runner 返回
  既有结果时收敛为同一 admitted plan；
- 物化 `Rejected`：post-plan rejection——Job cancelled、initial Turn cancelled、Session idle、
  无 visible link/input/callback、replay must-not-submit；
- 物化后 abort：`ReleaseAgentWorkspace` 以稳定 command 调用；release 未知时保留 pending，不产生
  孤儿 workDir；
- Server 永不构造/校验/读写 workDir 指向的目录；Runner 永不从环境或目录扫描猜 identity、永不
  为 child 选择不同 Runner；
- 生命周期：创建于 spawn、跨 Turn/follow-up 复用同一 workDir、独立路径与分支；detach/stop 不删
  worktree；`ChildLaunchJobId` 终态不立即让 worktree eligible（保留 follow-up 复用）；
- eligible 由 Server activity 权威决定：Runner 周期查询后，idle 超过阈值的标记 eligible，active
  的保持；parent 工作空间在有 active child worktree 时推迟清理；
- Runner 重启：worktree 完好则复用；parent 仓库丢失致 `.git` link 失效则 child 访问以
  `workspace-corrupt` 失败；不跨 Runner 迁移。

## 交付拆分

后续实现可拆成独立 issue，每个能用一句话说清独立交付价值：

1. **Runner managed-worktree 物化原语**：Runner 实现 `MaterializeAgentWorkspace` /
   `ReleaseAgentWorkspace`（git worktree of parent 工作空间、opaque identity、本地注册表、
   idempotency、parent-child 依赖推迟清理），配 fake clock/磁盘的 unit 覆盖。独立价值：Runner
   具备物化与回收隔离 worktree 的能力，可被任何调用方使用。
2. **Server spawn workspace mode 与物化接入**：CLI `--workspace`、API body、request fingerprint
   扩展、`worktree` mode 的接受条件与物化请求/失败收敛、post-plan rejection、Unknown 重试。
   依赖 1 的原语契约。独立价值：`mo agent spawn --workspace worktree` 端到端可用。
3. **工作空间生命周期与回收收敛**：Server activity 查询、Runner 周期 eligible 判定、storage
   budget 回收、release 信号。依赖 1、2。独立价值：managed worktree 不无限堆积，按 idle/保留
   回收。

## Status

交付增量 4（Managed worktree）**尚未实装**。当前全部代码事实：

- 子会话只继承 parent 的 authoritative workDir（增量 1）。`AgentSpawnAdmission.WorkDir` 取自
  `parent.Session.Runtime.WorkDir`；spawn body 只接受 `targetAgentRef` 与 `prompt`，无
  workspace mode。
- Runner 只物化 Issue-backed 的 WorkflowRun workspace（`WorkspaceManager.prepare`，键为
  `workflowRunId`，路径 `<runnerRoot>/workspaces/<workflowRunId>`，marker `{workflowRunId,
  runBranch}`）。不存在 `git worktree add` 形式的 child 工作空间物化，也没有 agent worktree
  注册表。
- 直接 launch 与 Agent Connection launch 的 `WorkspacePath = null`，无 workDir。
- Server→Runner 既有命令通道（`SessionCommand`、workspace 查询、follow-up 投递）可承载新的
  物化/释放原语，但当前不存在该原语。

本篇为 spec（目标设计）；以上差距由上述交付拆分的 issue 推进落地，落地后无需改本文。增量 1–3、5
的契约不受本篇影响，仍以 [`subagents.md`](subagents.md) 为权威。
