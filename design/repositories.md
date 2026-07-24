---
status: wip
---

# Repository 执行

Repository 是 Project Space 拥有的命名执行资源。Issue 只保存目标 Repository 的资源名；
WorkflowRun 不复制 Repository，Runner workspace 也不成为 Repository 身份的第二份真源。

## Model

```text
Project.Repository
  Name
  GitUrl
  BaseBranch
  IsDefault

Issue
  ProjectId
  RepositoryName
  WorkflowRunId?

WorkflowRun
  Id
  ProjectId
  IssueNumber

WorkspaceMarker
  WorkflowRunId
```

- Project Repository 是 `GitUrl` 与 `BaseBranch` 的唯一写入权威。
- Issue 的 `RepositoryName` 是对 Project Repository 的稳定引用。Issue 首次启动后不能改绑。
- WorkflowRun 只保存定位 Issue 所需的标量，不保存 Repository snapshot、workspace path 或
  branch。
- Workspace 是 Runner 的可重建执行状态。它没有独立业务身份；`WorkflowRunId` 足以标识
  它属于哪次运行。
- Git remote 的规范化结果是校验过程中的临时值，不是领域字段。系统不持久化
  `RemoteFingerprint` 或 `RemoteIdentityVersion`。

## Semantics

### Repository 变更

Project 可以新增 Repository、切换 default，并在没有未完成 Issue 使用时修改 `GitUrl`、
`BaseBranch` 或删除 Repository。

backlog 与 `in_progress` Issue 都会占用其目标 Repository：

- 修改该 Repository 的 `GitUrl` 或 `BaseBranch` 被拒绝；
- 删除该 Repository 被拒绝；
- 切换 Project default 不受影响，因为 Issue 已保存明确的 `RepositoryName`；
- done 与 cancelled Issue 只保留历史资源名，不阻止修改或删除。

Repository 更新与 Issue create、reassign、reopen、remove 必须经过同一个 Project-scoped
协调边界。它先查询未完成 Issue blocker，再提交 Project 修改。现有
`IssueRepositoryCoordinator` 已串行化这些绑定变化；把 metadata update 加入同一边界即可，
不需要为 Issue start 新增协调协议，因为 Issue 在 backlog 时已经构成 blocker。

同一 Project 不允许两个 Repository 名称指向等价的 Git remote。别名检查可以在写入时
临时规范化 URL 后比较，但不保存 hash。集成锁继续使用 `(ProjectId, RepositoryName)`；
资源名唯一对应一个 remote，使该锁不会把同一个物理仓库拆成两把锁。

### Dispatch

每次 task dispatch 按下面顺序构造 runtime context：

```text
WorkflowRun.(ProjectId, IssueNumber)
  -> Issue.RepositoryName
  -> Project.Repository
  -> repository.{name, gitUrl, baseBranch}
```

解析结果只进入本次 dispatch。它不是 Run Variables，也不写回 WorkflowRun。不存在 Project
default、`main` 或旧变量的 fallback。目标资源缺失时，task 以可操作的 Repository 错误
失败；修复 Project Repository 后可以 retry。

未完成 Issue 已锁定 Repository 执行属性，因此同一个 WorkflowRun 的各次 dispatch 会看到
稳定的 `GitUrl` 与 `BaseBranch`，不需要 snapshot。

### Workspace

Issue-backed workspace 使用系统生成的 `WorkflowRunId` 直接推导：

```text
path   = <runnerRoot>/workspaces/<workflowRunId>
branch = mohist/run-<workflowRunId>
marker = { "workflowRunId": "<workflowRunId>" }
```

Runner 只接受符合系统 ID 语法的 `WorkflowRunId`，然后检查目标路径位于 `runnerRoot` 下且
不是符号链接。Repository 名称、Issue 标题和其他用户输入不进入路径或 branch。

准备或复用 workspace 时，Runner 验证：

1. 路径与 branch 均由 dispatch 的 `WorkflowRunId` 推导；
2. marker 的 `WorkflowRunId` 与 dispatch 一致；
3. 当前 checkout 是预期 branch；
4. `git remote get-url origin` 与本次 dispatch 的 `repository.gitUrl` 一致。

Git URL 比较只需 trim 后精确相等。workspace 由该值 clone，Issue 未完成期间该值又禁止修改；
不需要跨 Server/Runner 维护另一套 URL 等价算法。用户手工修改 workspace 的 `origin` 属于
损坏，系统明确失败，不猜测两个不同写法是否指向同一仓库。

workspace marker 不保存 Project、Issue、Repository、base branch、run branch、remote hash
或算法版本。它们要么可以从权威状态读取，要么可以从 `WorkflowRunId` 推导。

首次创建或丢失后重建 workspace 时，Runner 先查询远端同名 run branch：

```text
origin/mohist/run-<workflowRunId> exists -> checkout 远端 branch
otherwise                                 -> 从 Repository.BaseBranch 创建 branch
```

因此已推送的 run branch 是 workspace 重建来源。尚未推送的本地提交不是持久状态；workspace
损坏或 Runner root 丢失后，Workflow 重新执行相应 task。

### Workspace 查询与清理

diff、commits、文件读取、rebase 和手动清理以 `WorkflowRunId` 寻址。Server 使用 ProjectId
选择 Runner，但 ProjectId 不进入 workspace identity。Runner 自己推导 path 与 run branch；
需要 base branch 的操作使用 dispatch 时解析出的 Project Repository。

Runner registry 只保存清理无法从其他地方推导的生命周期事实：

```text
WorkspaceRegistryEntry
  WorkflowRunId
  Phase: active | eligible | stuck
  MaterializedAt
  TerminalAt?
```

清理只删除由 `WorkflowRunId` 推导、位于 runner root 下、且 marker 匹配的目录。清理不要求
Repository 仍存在，也不校验 remote；Repository 内容不参与“这个目录是否可以删除”的判断。

## Failure Semantics

| Failure | Result |
|---|---|
| 未完成 Issue 使用 Repository 时修改 git URL / base branch | 拒绝 Project 更新，指出阻塞 Issue |
| dispatch 无法解析 Issue 的 Repository | task 失败，修复 Project 后 retry |
| workspace marker 缺失或 run ID 不符 | `workspace_corrupt`，不修改目录 |
| workspace branch 不符 | `workspace_branch_mismatch`，不自动切换未知 workspace |
| workspace origin 与 Project Repository 不符 | `workspace_repository_mismatch`，不 fetch/push/rebase |
| cleanup 目标不在 runner root 或 marker 不符 | 拒绝删除并将 registry entry 标为 stuck |

## Rollout

本项目不保留旧 workspace 协议兼容层。Server 与 Runner 必须作为同一版本部署：

1. 部署前停止 Server 与 Runner，并备份数据库和 Runner root；
2. 清空 Runner workspace registry；
3. 删除没有需要保留提交的旧 workspace；
4. 对仍有未合入提交的旧 run，先确认远端 branch 已包含提交；
5. 启动同一版本的 Server 与 Runner；
6. retry 原 run，确认 Runner 从远端同名 branch 重建 workspace，且新 marker 只含
   `workflowRunId`。

不增加 legacy snapshot 回填、旧 marker 升级或 fingerprint fallback。可重建状态直接重建；
必须保留的 Git 工作先通过远端 branch 保存。

## Status

当前实现与目标设计的主要差距：

- `WorkflowRun` 持有 `WorkflowRepositoryContext` 与 `WorkspaceIdentity`；
- `IssueWorkStarted`、dispatch overlay、workspace API 在多层复制 Repository snapshot；
- Runner marker、registry 与 query 重复保存 Project、Issue、Repository、branch、fingerprint
  和版本；
- Project Repository 的 `GitUrl` / `BaseBranch` 更新没有使用未完成 Issue blocker；
- workspace path 使用 run hash，marker 需要完整身份字段。

落地时先建立 Repository 占用锁定，再一次性切换 Server/Runner workspace 协议，最后删除
旧模型与测试。协议切换不能拆成两个独立部署。
