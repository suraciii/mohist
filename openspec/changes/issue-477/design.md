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
  目标写入方必须重新验证自身不变量。`IssueRepositoryCoordinatorGrain` 串行化 Issue
  selection 与 repository lifecycle；`WorkflowProfileReferenceCoordinator` 串行化 Project default、
  Run binding 与 Profile deletion（见 Decision 4），每次命令仍只写一个目标聚合。
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
`Name`、`Description`、Definition YAML source、source provenance 及审计时间。新建或编辑的
custom Profile 原样保存并返回提交的 YAML；legacy `WorkflowDefinition` JSON 已丢失注释、格式、
alias 与原始 ordering，migration 必须以固定的 canonical YAML renderer 从语义模型生成 source，并
标记为 `canonical-legacy`。`--yaml` 对这类 Profile 返回该 canonical source，而不宣称恢复原文。
将 `ProjectWorkflowTemplates` 的自定义项迁入此表；Profile ID 在创建后不可改名，`/` 是合法 ID
字符。内置 `mohist/*` 继续由 versioned builtin asset/catalog 提供，但由 collection provider 与
自定义项一起合并、排序和读取，故对调用方是同一个 collection。

Provider 暴露窄接口：列举、按 `(projectId, profileId)` 读取、验证自定义写入、以及取得当前
`WorkflowDefinition`。它拒绝创建 `mohist/*`，并对内置 ID 的更新与删除返回 read-only 领域
错误。所有读取和选择均通过 provider 判断 ID 是否属于当前 Project collection；不再让调用方
直接探测 system catalog 或 legacy template table。公开和领域模型只保存一个 Profile ID；持久化行另有
nullable custom-Profile key backing column，仅当 ID 属于 custom Profile 时写入，作为 Project default、
Issue selection 与活动 Run binding 到 custom `(ProjectId, ProfileId)` 的 restrictive foreign-key target。
Run 在自己的终态转换事务中仅清除该 backing key，保留公开 Profile ID 及所有历史事实；builtin IDs 的
backing column 为 null，且不可删除，故不需要 foreign key。

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

Project default 与 WorkflowRun binding 通过 `WorkflowProfileReferenceCoordinator` 在各自队列位置
调用 collection provider 验证存在性。Issue create/edit 留在 `IssueRepositoryCoordinatorGrain`：它
在 `IIssueBindingParticipant` 的单一 Issue 事务内重新验证 Profile（与 repository 存在性同一入口）
并提交 selection。对 custom Profile，nullable backing column 的 restrictive foreign key 是并发删除的
最终事务保护；对 builtin Profile，该列为 null，provider 的 read-only 生命周期保证不存在删除竞争。
`issue edit --inherit-workflow-profile` 仅清除 Issue 字段；同时传该 flag 与
`--workflow-profile` 由 CLI 的互斥 option 在发请求前拒绝。修改 Project 或 Issue 引用绝不写入
已有 WorkflowRun。

选择显式 nullable 引用而非 sentinel，避免与合法 slash-capable Profile ID 冲突，也使继承语义
在持久模型中可读。备选方案是让 WorkflowRun 每次 Stage 初始化重新读取 Issue/Project 选择；
它会让选择更新重绑活动 Run，违反 Run 启动绑定要求，因此不采用。

### 3. Run 按绑定 ID 实时解析，Stage 仍在初始化时物化

将 `WorkflowProfileManager` 的 template cascade 收敛为 `IWorkflowProfileProvider` 的按 Run
绑定读取：Run 创建先读取 selected Profile 的结构，以该时刻的 Stage name 和 declaration order
创建完整的 Stage lifecycle。这个 Stage topology 是 Run 的不可变事实；它不保存 Definition
snapshot，但后续 Profile edit 不会新增、移除或重排 Run 的 Stage lifecycle。每次尚未初始化的
既有 Stage 进入初始化路径时，按 Run 的 `(ProjectId, WorkflowProfileId)` 获取当前 Definition，再按
该 Stage name 构建任务、checks 和 lock behavior。不能从 Issue 或 Project 重新选择 Profile。

Stage 初始化完成后，产生的 StageRun/TaskRun/attempt 和结果继续作为 WorkflowRun 持久状态，
后续 Profile 编辑不得回写它们。编辑在当前 Definition 新增的 Stage 不会被这个 Run 调度；重排
既有 Stage 不改变 Run 的启动顺序。Definition 中移除已初始化 Stage 不追溯影响其历史；缺少未来
待初始化 Stage 时，该 Stage 初始化以明确领域错误失败并让 Run 走现有失败可见路径，不以历史
Definition 或静默回退掩盖问题。

选择 ID binding 加按 Stage live resolution，是唯一同时满足活动 Run 可更新后续阶段和历史事实
不被追溯的模型。备选的完整 snapshot 会隔离编辑但阻断新 Definition；按 task 读取 Definition
会让同一已初始化 Stage 内的任务发生不可预测变化，两者均不采用。

### 4. 两个 coordinator 保持既定边界，外键封住跨队列删除竞争

保留 `design/architecture.md` 的两个 Project-scoped coordinator。`IssueRepositoryCoordinatorGrain`
继续是 Issue create、repository reassignment、cancelled Issue reopen、repository removal，以及 Issue
explicit Profile selection（含 edit / `--inherit-workflow-profile` 清除）的唯一入口。它调用
`IIssueBindingParticipant`，使 Issue create 在同一 Issue 事务内提交 repository binding 与 Profile
selection。`WorkflowProfileReferenceCoordinator` 只串行化 Project default 更新、WorkflowRun startup
binding 与 Profile 删除；它不接管 Issue lifecycle，也不调用 Issue coordinator。

两者都只持久化不确定投递的 command fence，参与者以 command ID 和 expected revision 幂等接受重投。
它们不保存 Profile、Project、Issue 或 Run 的业务事实，也不在一个事务内写多个聚合。Profile
coordinator 在队列位置验证 Project default 或 Run binding 的 collection membership，再只调用
`IProjectBindingParticipant` 或 `IWorkflowRunBindingParticipant`。Issue coordinator 的
`IIssueBindingParticipant` 在自己的 Issue transaction 内重新验证 membership 后提交 Issue selection。

删除时，Profile coordinator 先对 builtin 返回 read-only，再由
`WorkflowProfileDeletionBlockerQuery` 汇总 Project default、该 Project 的所有 Issue（包括终态 Issue）
explicit selection，以及非终态 WorkflowRun binding。存在任一引用即拒绝，错误返回全部阻塞关系及可
辨识的 Issue number / WorkflowRun ID；不只返回第一项。删除命令只删除 Profile 自己的 custom record，
Project、Issue 和 Run 不被同时修改。

`WorkflowProfileDeletionBlockerQuery` 是可操作诊断而非并发正确性的唯一依赖。Project default、Issue
selection 与活动 Run 的每个 custom Profile reference persistence row 填写 nullable backing key，并使用指向
`(ProjectId, ProfileId)` 的 restrictive foreign key；WorkflowRun 在转为终态的自身事务中只清除该 backing
key，仍保留公开 Profile ID 与历史。builtin reference 的 backing key 保持 null。Profile 删除与来自另一 coordinator 的 Issue
selection 在数据库事务中竞争时，先提交的 Issue reference 令 delete 受 FK
拒绝，随后重新查询并返回 blocker；先提交的 delete 令 Issue insert/update 受 FK 拒绝，映射为可重试的
`workflow-profile-not-found` conflict，且不提交 dangling reference。Project-default 与 Run-binding 已由
Profile coordinator 串行，foreign key 同时防止任何意外绕过。builtin Profile 不可删除，provider 是其
唯一存在性和 read-only 权威，因此它们不需要外键行。

ArchTest 分别禁止绕过两个 coordinator：Issue 的 Profile selection 和 repository lifecycle 只能由
`IssueRepositoryCoordinatorGrain` 消费，Project default / Run binding / delete participant 接口只能由
`WorkflowProfileReferenceCoordinator` 消费。两个 coordinator 不互调、不共享事务，符合架构定义的单向
participant 调用。

备选方案一是把 Issue selection 迁入 Profile coordinator；它与既定 Issue/repository lifecycle
边界冲突，并要求未规划的 coordinator consolidation。备选方案二是 coordinator 同步互调；它会形成
coordinator 链并违反「不得引入同步回调环」。备选方案三是删除时自动清除 default/Issue 引用或终止
Run；它会隐式修改多个聚合且可能改变正在执行的工作。三者均不采用。

### 5. 保存复用两段权威校验，诊断保留来源

Profile create/edit 接收 Profile metadata 和 Definition source。Server 先调用 #432 parser，
仅在得到语义模型后把模型交给 #446 Action catalog validator；两类 diagnostics 以 YAML path
返回，并保留 `Definition` 或 `Action` source。无可用 catalog 时沿用 #446 的显式 skipped
状态，不阻止保存；Runner 仍在执行入口 fail-closed。

`workflow view <profile> --yaml` 对 `verbatim` source 直接输出保存的原始 Definition YAML，对
`canonical-legacy` source 输出 migration 生成并保存的 canonical YAML；`--yaml` 与 `--json` 在
System.CommandLine 层互斥。它是 Profile 的 source view，而非通用 renderer。CLI 只负责文件/stdin
输入、互斥校验、调用 API 和渲染结果。

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

Server spec tests 覆盖同 Project collection、跨 Project 隔离、builtin read-only、verbatim 新 source 与
canonical legacy source、两类保存错误来源、Project 创建时 `mohist/local` default、全部删除阻塞关系、
终态转换仅清除 Run backing key 后可删除仅被终态 Run 引用的 Profile 且 Run 历史不变，participant fake
验证 Profile coordinator 内 Project-default / Run-binding / deletion 的顺序；另用真实
transactional in-memory relational provider 验证 Issue coordinator 的 selection 与 deletion 的交错：
Issue reference 先提交则 delete 返回 blocker，delete 先提交则 Issue transaction 返回 retryable conflict
且不提交。测试须包含终态 Issue 引用的 blocker。WorkflowRun spec 必须证明 Profile/selection 更新不会
改变已初始化 Stage、attempt 或历史。

CLI spec tests 用 fake HTTP 验证命令路径、slash ID 编码、`--yaml`/`--json` 和 Issue flags 的
本地互斥、请求 body、JSON fields、错误和帮助；不复制 server validator 测试。迁移后删除或
改写覆盖 legacy command/API 的测试，避免让已移除 surface 继续成为公开契约。测试不使用真实
网络、进程、数据库或墙钟。

## Risks / Trade-offs

- [删除检查与 Issue selection 位于不同 coordinator，存在跨队列竞态] -> 保持架构规定的 coordinator
  边界：Profile coordinator 串行 Project default / Run binding / delete，Issue coordinator 串行 Issue
  selection 与 repository lifecycle。custom Profile 的 nullable backing-key restrictive foreign key 把最终
  提交竞争收敛为 blocker 或 `workflow-profile-not-found` conflict，绝不留下悬空引用；builtin immutable，
  故无 delete race。
- [编辑绑定 Profile 可使未来 Stage 与先前 Stage 的 Definition 不同] -> 这是明确产品语义；
  Run 在启动时固定 Stage name 和顺序，后续编辑只改变尚未初始化的既有 Stage 内容，新增和重排
  不改变该 Run，移除未来 Stage 会使其在初始化时可见失败。`workflow edit` help 提示其可能影响
  活动 Run，Run 保留 Profile ID 和已初始化事实以便审计。
- [旧 Issue inline Definition 无法无歧义映射为共享 Profile] -> 迁移时为每个仍有 inline
  Definition 的 Issue 生成稳定、Project 内唯一的自定义 Profile，并将 Issue 指向它；不将
  Definition 留在 Issue 表。
- [legacy JSON 已无法恢复提交时的 YAML] -> migration 以固定 canonical YAML renderer 生成并持久化
  `canonical-legacy` source；新建和编辑保留 verbatim source。API/CLI 明确这一区别，不把 canonical
  output 表述为原始输入。
- [legacy custom ID 使用现已保留的 `mohist/*` namespace] -> migration 将其重命名为
  `legacy-reserved/{base64url-utf8(originalProfileId)}`，并重写同 Project 的 default、Issue reference、
  inline-derived reference 与 Run binding。若 Project 已有不同 custom Profile 使用该确定目标 ID，migration
  在写入前失败并报告 Project、source ID 与 target ID，不执行部分迁移；操作者须先修复冲突后重试。
- [builtin asset 不是数据库行，关系约束无法由外键表达] -> custom Profile 引用由 nullable
  backing-key restrictive foreign key 保护；builtin 引用的 backing key 保持 null，因其不可删除，
  存在性与 read-only 判定统一由 collection provider 负责，不依赖关系数据库单独保护。
- [移除 legacy API/CLI 会破坏已自动化的调用方] -> 本项标记为 breaking；同一发布更新 CLI、
  help、docs 和 tests，不提供 alias，调用方按新唯一 surface 迁移。
- [catalog 暂不可用时保存的 Profile 可能在 Runner 失败] -> 返回明确 `actionValidation` skipped
  状态；Runner 继续以本地 manifest fail-closed，防止未经验证的输入执行。

## Migration Plan

1. 增加 collection、source provenance、Project/Issue/Run ID 引用字段、仅 custom ID 填写的 nullable
   foreign-key backing columns、restrictive foreign keys 和必要索引的 EF migration；保留独立的
   Variables/Prompts 存储，不把它们迁入 Profile。
2. 对每个 legacy custom template 或 inline Definition，以固定 canonical YAML renderer 从持久的
   semantic JSON 生成 source 并标记 `canonical-legacy`。新 API 后续 create/edit 写入 `verbatim` source；
   migration 不声称能恢复 legacy 原始 YAML。
3. 迁移每个 `ProjectWorkflowTemplates` custom Profile。非 `mohist/*` ID 保留原 ID；`mohist/*` ID
   改为 `legacy-reserved/{base64url-utf8(originalProfileId)}`。迁移在写入前检测该 target ID 是否已被
   不同 Profile 占用；若占用则以 Project、source ID、target ID 报错并整体失败，不留部分结果。将
   Project default、Issue template reference、inline-derived reference 和 Run binding 全部重写为迁移后
   ID。缺少 legacy Project default 而原先会落到 system fallback 的 Project 写入 `mohist/local`；内置
   ID 保持内置引用。
4. 对所有现存 Run，根据迁移前有效级联解析一次并写入迁移后的 Profile ID；若来源为 inline
   Definition，复用步骤 2 创建的 Profile。活动 custom Run 写入 restrictive foreign-key backing key；已是终态
   的 custom Run 写入 null backing key，只保留公开 Profile ID 和既有历史持久状态，不补写 Definition
   snapshot；后续允许删除仅被终态 Run 引用的 Profile。
5. 切换 Server provider、`WorkflowProfileReferenceCoordinator`（Project default、Run binding 与
   delete）、`IssueRepositoryCoordinatorGrain`（Issue selection 与既有 repository lifecycle）、Run
   创建/Stage 初始化、collection API 和引用保护到新模型，随后删除旧 cascade、inline write path 和
   template-only APIs。Issue coordinator 调用 `IIssueBindingParticipant.CreateAsync`，其同一 Issue
   事务仍一并提交 repository binding 与 Profile 引用。
6. 切换 CLI 到 `mo workflow`、`project workflow set-default` 与 Issue selection flags，并同时
   更新 docs/help/spec tests；发布后移除旧 routes、commands、DTO 和测试。

部署前对迁移执行计数和抽样校验：每个旧 custom template/inline Definition 都有一个可读取的
Profile 且标记正确的 source provenance，全部 reserved-ID rename 都完成 reference rewrite，所有
default/Issue/active Run 引用都能在其 Project collection 中解析。若发现迁移错误，
在删除旧表和旧代码前回滚应用版本与 migration，并从备份恢复 legacy rows；发布完成并删除旧
storage 后，只能通过数据库备份恢复，不能由兼容代码回退。

## Open Questions

- 无。Profile source 的数据库列类型、migration 名称和 DTO 字段名由实现沿用现有 persistence/
  API 命名约定确定，不改变本文的领域语义。
