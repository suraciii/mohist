## Context

#477 将 Workflow Profile 从 system catalog、Project template、Issue inline/reference template
和默认配置的多层级联，收敛为 Workflow 核心域拥有、以 `ProjectId` 为 tenancy boundary 的
`WorkflowProfile` collection。动机与产品范围见 [proposal.md](proposal.md)，行为要求见
`specs/workflow-profile-*`。

现有 `WorkflowProfileManager` 会在运行时重新计算 Issue custom template、Issue template
引用、Project default 和 system profile 的级联结果。`ProjectWorkflowTemplates` 保存自定义
Profile，`IssueWorkflowProfiles` 还能保存 inline Definition，`WorkflowRun` 没有选中 Profile
的持久绑定。这些模型会使删除保护和活动 Run 的 Definition 来源不明确。

约束如下：

- Workflow 保持自治；Issue 和 Project 只保存 Profile ID 引用，不复制 Definition body。
- 单个聚合事务不能同时写 Profile、Project、Issue 和 WorkflowRun。跨聚合查询只能辅助选择，
 目标写入方必须重新验证自身不变量。所有会建立或破坏 Profile 引用的命令必须经过同一个按
  Project 串行的 coordinator（见 Decision 4），但每次命令仍只写一个目标聚合。
- #432 的 `WorkflowDefinition` parser 是 Definition 语义的唯一权威；#446 的 Action catalog
  是 `uses` / `with` 契约的唯一权威。CLI 不实现两者的副本。
- 活动 Run 的已初始化 Stage、已接受 attempt 与历史结果是不可变事实；未初始化 Stage 仍要
  读取该 Run 已绑定 Profile 的当前 Definition。
- Project、Issue、Run、CLI 和 Web API 是主要调用方；Runner 不参与 Profile 选择或校验。

## Goals / Non-Goals

**Goals:**

- 建立 Project-scoped `WorkflowProfile` collection，统一内置与自定义 Profile 的读取、选择和管理。
- 将 Project default、Issue explicit selection 和 WorkflowRun startup binding 表达为同一 collection
  的 ID 引用。
- 让 Profile 更新即时影响同 ID Profile 所绑定活动 Run 的后续 Stage 初始化，同时冻结已物化的
  Run 事实。
- 用明确、可操作的领域错误保护内置项与仍被引用的自定义 Profile。
- 用 `mo workflow`、`mo project workflow set-default` 和 issue Profile flags 替换旧命令面，并在
  同一变更中迁移对应 API、帮助、文档和契约测试。

**Non-Goals:**

- 不增加 Definition version、Run Definition snapshot、Profile 继承或 Profile 合并。
- 不将 Variables 或 Prompts 放入 Profile，或恢复 Issue inline Definition 的写入能力。
- 不改变 Runner 的 dispatch、Action manifest 校验或已有 Definition DSL。
- 不为旧 `project workflow template`、`project workflow config` 或 `issue workflow config` 保留
  alias、重定向或平行 API。

## Decisions

### 1. Collection 是唯一 Profile 真源

新增专用的 Project-scoped persistence shape：每项以 `(ProjectId, ProfileId)` 标识，保存
`Name`、`Description`、原始 Definition YAML（或能无损返回的同等 source）及审计时间。将
`ProjectWorkflowTemplates` 的自定义项迁入此表；Profile ID 在创建后不可改名，`/` 是合法 ID
字符。内置 `mohist/*` 继续由 versioned builtin asset/catalog 提供，但由 collection provider 与
自定义项一起合并、排序和读取，故对调用方是同一个 collection。

Provider 暴露窄接口：列举、按 `(projectId, profileId)` 读取、验证自定义写入、以及取得当前
`WorkflowDefinition`。它拒绝创建 `mohist/*`，并对内置 ID 的更新与删除返回 read-only 领域
错误。所有读取和选择均通过 provider 判断 ID 是否属于当前 Project collection；不再让调用方
直接探测 system catalog 或 legacy template table。

选择此方案是因为 Profile 是独立、可稳定寻址且由 Workflow 拥有的资源。备选方案是继续用
Project template 加 system catalog 的聚合 read model；它会保留两套写入/存在性规则，无法给
Profile 引用和删除检查提供单一语义，因此不采用。

### 2. 引用只保存 ID，并在各自所有者内校验

Project 的 `DefaultWorkflowProfileId` 替换 `DefaultTemplateId`，且为必填：创建 Project 时写入
内置 `mohist/local`。内置 catalog 必须始终提供该 ID；若它无法读取，Project 创建失败，不产生
没有可启动 Profile 的 Project。Issue 的可选 `WorkflowProfileId` 表示 explicit selection，`null`
表示 inherit；WorkflowRun 增加必填 `WorkflowProfileId`，在 Run 创建时写入。Run 创建按以下规则
选择并持久化，而不是在每次运行时重算：

```text
selectedProfileId = issue.workflowProfileId ?? project.defaultWorkflowProfileId
assert collection.contains(projectId, selectedProfileId)
workflowRun.workflowProfileId = selectedProfileId
```

Project default、Issue create/edit 与 WorkflowRun binding 都通过
`WorkflowProfileReferenceCoordinator` 在各自队列位置调用 collection provider 验证存在性；Issue
create/edit 随后由 `IIssueBindingParticipant` 在其单一 Issue 事务内提交（与验证 repository
存在性同一入口）。
`issue edit --inherit-workflow-profile` 仅清除 Issue 字段；同时传该 flag 与
`--workflow-profile` 由 CLI 的互斥 option 在发请求前拒绝。修改 Project 或 Issue 引用绝不写入
已有 WorkflowRun。

选择显式 nullable 引用而非 sentinel，避免与合法 slash-capable Profile ID 冲突，也使继承语义
在持久模型中可读。备选方案是让 WorkflowRun 每次 Stage 初始化重新读取 Issue/Project 选择；
它会让选择更新重绑活动 Run，违反 Run 启动绑定要求，因此不采用。

### 3. Run 按绑定 ID 实时解析，Stage 仍在初始化时物化

将 `WorkflowProfileManager` 的 template cascade 收敛为 `IWorkflowProfileProvider` 的按 Run
绑定读取：Run 创建先读取 selected Profile 的结构以创建 Stage 生命周期；每次尚未初始化的
Stage 进入初始化路径时，按 Run 的 `(ProjectId, WorkflowProfileId)` 获取当前 Definition，再按
Stage name 构建任务、checks 和 lock behavior。不能从 Issue 或 Project 重新选择 Profile。

Stage 初始化完成后，产生的 StageRun/TaskRun/attempt 和结果继续作为 WorkflowRun 持久状态，
后续 Profile 编辑不得回写它们。Definition 中移除已初始化 Stage 或缺少未来待初始化 Stage 时，
初始化以明确领域错误失败并让 Run 走现有失败可见路径，不以历史 Definition 或静默回退掩盖
问题。

选择 ID binding 加按 Stage live resolution，是唯一同时满足活动 Run 可更新后续阶段和历史事实
不被追溯的模型。备选的完整 snapshot 会隔离编辑但阻断新 Definition；按 task 读取 Definition
会让同一已初始化 Stage 内的任务发生不可预测变化，两者均不采用。

### 4. 一个 coordinator 串行化所有 Profile 引用与删除

新增 Project-scoped `WorkflowProfileReferenceCoordinator` 是所有 Profile reference-writing 命令
及删除的唯一入口：Project default 更新、WorkflowRun 启动 binding、Issue explicit selection（含
create / edit / `--inherit-workflow-profile` 清除）和 Profile 删除均以 Project key 排队。它取代
`IssueRepositoryCoordinatorGrain` 对 Issue create/edit 的入口职责；后者随旧 repository-only
协调路径删除或收敛，不得继续作为并行入口。这样 Issue 选择与删除拥有一个共享的、持久且可恢复
的顺序，而非依赖过期查询或跨 coordinator 复验。

为保留既有 repository-binding 的串行化，原 `IssueRepositoryCoordinatorGrain` 的其余 Issue
repository lifecycle 命令也必须一并迁入该 Coordinator，或将该 grain 收敛为它的同一实现；不得
保留能与 Issue create 并发的第二条 repository-binding 入口。

Coordinator 持久化不确定投递的 command fence；参与者以 command ID 和 expected revision 幂等
接受重投。它不保存 Profile、Project、Issue 或 Run 的业务事实，也不在一个事务内写多个聚合。
每个队列项在其位置验证 Profile 属于 collection，再只调用对应的唯一引用拥有者：
`IProjectBindingParticipant`、`IIssueBindingParticipant` 或 `IWorkflowRunBindingParticipant`。Issue
create 仍调用 `IIssueBindingParticipant.CreateAsync`，该参与者在同一个 Issue 事务内提交 repository
binding 与 Profile 引用，故既有 Issue-create repository-binding 不变量不变；只是该命令由共享
Coordinator 投递，避免第二个 create owner。

对删除，Coordinator 在队列位置由 `WorkflowProfileDeletionBlockerQuery` 汇总 Project default、该
Project 的所有 Issue（包括终态 Issue）explicit selection，以及非终态 WorkflowRun binding；存在任一
引用即拒绝，错误返回全部阻塞关系及可辨识的 Issue number / WorkflowRun ID；不只返回第一项。
内置 Profile 在引用查询前即因 read-only 拒绝。删除命令只删除 Profile 自己的记录，Project、Issue
和 Run 不被同时修改。

共享队列给出可串行化的顺序：reference write 先提交时，随后删除观察到该引用并拒绝；删除先
提交时，随后 reference write 在其队列位置重新验证并得到可重试的
`workflow-profile-not-found` conflict，且不提交引用。该保证也覆盖 Issue 参与者的存在性校验与提交
之间的窗口，因为删除无法插入同一 Coordinator 已取得的队列位置。客户端以同一 command ID 重试
不重复写入；以新命令重试前必须刷新 collection 并选择可用 Profile。数据库外键不指向内置项，
不能单独承担该规则。

直接调用 Profile、Project、Issue 或 Run 的 reference-writing 接口（绕过
`WorkflowProfileReferenceCoordinator`）由 ArchTest 禁止；该规则沿用现有
`BindingParticipantInterfaces_OnlyConsumedByCoordinator` 的命名约定，新参与者接口置于
`*.Grains.Coordinator` 命名空间即自动受其约束。

备选方案一：让 Issue selection 继续留在独立 coordinator，再让删除读取 Issue 投影并依赖参与者
存在性复验；它不能覆盖 deletion check、Issue revalidation 与 Issue commit 的交错。备选方案二：
让两个 coordinator 同步互调；它会形成 coordinator 链并违反「不得引入同步回调环」。备选方案三：
删除时自动清除 default/Issue 引用或终止 Run；它会隐式修改多个聚合且可能改变正在执行的工作。
三者均不采用。

### 5. 保存复用两段权威校验，诊断保留来源

Profile create/edit 接收 Profile metadata 和 Definition source。Server 先调用 #432 parser，
仅在得到语义模型后把模型交给 #446 Action catalog validator；两类 diagnostics 以 YAML path
返回，并保留 `Definition` 或 `Action` source。无可用 catalog 时沿用 #446 的显式 skipped
状态，不阻止保存；Runner 仍在执行入口 fail-closed。

`workflow view <profile> --yaml` 直接输出保存的原始 Definition YAML，`--yaml` 与 `--json`
在 System.CommandLine 层互斥。它是 Profile 的 source view，而非通用 renderer。CLI 只负责
文件/stdin 输入、互斥校验、调用 API 和渲染结果。

备选方案是 CLI 调用 Definition library 或 catalog 后本地预校验。即使能复用程序集，也会制造
网络 catalog 与 Server 保存结果的双重权威，且无法保证错误一致性，因此不采用。

### 6. 以新 API 和命令树一次性替换 legacy surface

Server 提供 `/api/projects/{projectRef}/workflow-profiles` collection CRUD，单项 route 使用
terminal catch-all `{*profileId}` 保留 `/`；同一 Project resource 提供 default Profile 更新。
旧 workflow-template、system-profile aggregation、Issue template write endpoints 及其 DTO/
handler 在迁移完成后删除。Issue create/edit 的现有请求模型仅保留 optional
`workflowProfileId`，并新增清除 explicit selection 的明确 update 表示；不得把 inline template
字段保留为兼容入口。

CLI 将顶层 `workflow` 设为 collection 的唯一 CRUD area：`list/view/create/edit/delete`，并保留
本地 `workflow validate --file`。`project workflow set-default <profile>` 是 Project 引用的唯一
命令，issue create/edit 使用 selection flags。帮助、字段选择、table view、stdout/stderr 与
non-interactive 语义复用 #475 的共享契约；旧 group、alias 和测试一并移除。

备选方案是把新 collection 放在 `project workflow profile` 下或保留旧命令为 alias。前者与
CLI spec 中 `workflow` 对 WorkflowProfile 的唯一导航相悖，后者会长期保留两套契约，均不采用。

### 7. 用行为边界组织测试

Server spec tests 覆盖同 Project collection、跨 Project 隔离、builtin read-only、两类保存错误
来源、Project 创建时 `mohist/local` default、全部删除阻塞关系、default/Issue 的存在性校验，
以及 Run 的 ID binding 与 Stage live resolution。用可控 command fence 与 participant fake 在同一
Coordinator 中安排 Issue selection、Project-default write、Run-binding write 和 deletion 的
check/validate/commit interleavings：写入先取得队列位置时删除必须在提交后报告 blocker；删除先
取得队列位置时写入必须在删除后得到 retryable conflict，且参与者不得提交。测试须包含终态 Issue
引用的 blocker。WorkflowRun spec 必须证明 Profile/selection 更新不会改变已初始化 Stage、attempt 或历史。

CLI spec tests 用 fake HTTP 验证命令路径、slash ID 编码、`--yaml`/`--json` 和 Issue flags 的
本地互斥、请求 body、JSON fields、错误和帮助；不复制 server validator 测试。迁移后删除或
改写覆盖 legacy command/API 的测试，避免让已移除 surface 继续成为公开契约。测试不使用真实
网络、进程、数据库或墙钟。

## Risks / Trade-offs

- [删除检查与并发选择/Run 创建之间存在竞态] -> 一个按 Project 串行的
  `WorkflowProfileReferenceCoordinator` 接收所有 Profile 引用写入和删除；其持久 fence 建立
  Issue selection、Project default、Run binding 与删除的共享顺序。`WorkflowProfileDeletionBlockerQuery`
  汇总 Project default、所有 Issue 选择和活动 Run binding，删除后的写入在队列位置重新验证并返回
  `workflow-profile-not-found` conflict，绝不留下悬空引用。
- [编辑绑定 Profile 可使未来 Stage 与先前 Stage 的 Definition 不同] -> 这是明确产品语义；
  `workflow edit` help 提示其可能影响活动 Run，Run 保留 Profile ID 和已初始化事实以便审计。
- [旧 Issue inline Definition 无法无歧义映射为共享 Profile] -> 迁移时为每个仍有 inline
  Definition 的 Issue 生成稳定、Project 内唯一的自定义 Profile，并将 Issue 指向它；不将
  Definition 留在 Issue 表。
- [builtin asset 不是数据库行，关系约束无法由外键表达] -> collection provider 统一存在性与
  read-only 判定，Project/Issue/Run 写入均调用它，不依赖关系数据库单独保护。
- [移除 legacy API/CLI 会破坏已自动化的调用方] -> 本项标记为 breaking；同一发布更新 CLI、
  help、docs 和 tests，不提供 alias，调用方按新唯一 surface 迁移。
- [catalog 暂不可用时保存的 Profile 可能在 Runner 失败] -> 返回明确 `actionValidation` skipped
  状态；Runner 继续以本地 manifest fail-closed，防止未经验证的输入执行。

## Migration Plan

1. 增加 collection、Project/Issue/Run ID 引用字段和必要索引的 EF migration；保留独立的
   Variables/Prompts 存储，不把它们迁入 Profile。
2. 数据迁移将 `ProjectWorkflowTemplates` 逐项复制为同 ID 的 custom Profile，将 Project default
   和 Issue template reference 改为 Profile ID。缺少 legacy Project default 而原先会落到 system
   fallback 的 Project 写入 `mohist/local`；为 Issue inline Definition 创建独立 custom Profile 并
   替换为该引用；内置 ID 保持内置引用。
3. 对所有现存 Run，根据迁移前有效级联解析一次并写入 Profile ID；若来源为 inline
   Definition，复用步骤 2 创建的 Profile。终态 Run 同样只保留 ID 和既有历史持久状态，
   不补写 Definition snapshot；后续允许删除仅被终态 Run 引用的 Profile。
4. 切换 Server provider、`WorkflowProfileReferenceCoordinator`（所有 Issue selection、Project
   default、Run binding 与 delete）、Run 创建/Stage 初始化、collection API 和引用保护到新模型，
   随后删除旧 cascade、inline write path 和 `IssueRepositoryCoordinatorGrain` 的并行 create/edit
   入口。Coordinator 调用 `IIssueBindingParticipant.CreateAsync`，其同一 Issue 事务仍一并提交
   repository binding 与 Profile 引用。
5. 切换 CLI 到 `mo workflow`、`project workflow set-default` 与 Issue selection flags，并同时
   更新 docs/help/spec tests；发布后移除旧 routes、commands、DTO 和测试。

部署前对迁移执行计数和抽样校验：每个旧 custom template/inline Definition 都有一个可读取的
Profile，所有 default/Issue/active Run 引用都能在其 Project collection 中解析。若发现迁移错误，
在删除旧表和旧代码前回滚应用版本与 migration，并从备份恢复 legacy rows；发布完成并删除旧
storage 后，只能通过数据库备份恢复，不能由兼容代码回退。

## Open Questions

- 无。Profile source 的数据库列类型、migration 名称和 DTO 字段名由实现沿用现有 persistence/
  API 命名约定确定，不改变本文的领域语义。
