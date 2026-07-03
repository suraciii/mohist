## Context

Session 是横向叶子域（`design/domain-analysis.md`、`design/context-map.md`），硬约束是"自身不反向依赖任何业务上下文"。但它的读侧代码寄生在 `packages/server/src/Mohist.Server/Workflow/Services/Sessions/` 下，共 6 个文件：

| 文件 | 行数 | 当前 namespace | 目标位置 |
|---|---|---|---|
| `AgentSessionQueryMetadataKeys.cs` | 14 | `Mohist.Server.Workflow.Services.Sessions` | `Sessions/Services/` |
| `GenericAgentSessionMetadata.cs` | 99 | 同上 | `Sessions/Services/` |
| `WorkflowAgentSessionMetadata.cs` | 53 | 同上 | `Sessions/Services/` |
| `SessionTranscriptBuilder.cs` | 211 | 同上 | `Sessions/Services/` |
| `AgentSessionQuerier.cs` | 1510 | 同上 | `Sessions/Services/` |
| `AgentSessionReadModels.cs` | 391 | 同上 | `Sessions/`（DTO 层） |

物理错位导致领域核心知识 `AgentSessionQueryMetadataKeys`（session label schema 常量）寄住在 Workflow 目录，迫使 `Sessions/Services/AgentSessionQuery.cs:6` 反向 `using Mohist.Server.Workflow.Services.Sessions` 才能拿到它——叶子域反向依赖了 Workflow 目录。

**当前唯一现存反向 using**（`rg "using Mohist\.Server\.Workflow" src/Mohist.Server/Sessions/`）：

- `Sessions/Services/AgentSessionQuery.cs:6` —— `using Mohist.Server.Workflow.Services.Sessions;`（用于 `AgentSessionQueryMetadataKeys`）

**消费方 footprint**（`rg -l "Mohist\.Server\.Workflow\.Services\.Sessions"`）：约 14 个 src 消费方（含 Api 路由 9 个、`WorkflowGrain`、`WorkflowSessionHealthService`、`WorkflowActivityQuerier`、`MohistDbContext`、`AgentSessionQuery`）+ 约 23 个 test 文件。

**约束**：纯物理搬迁。不改业务逻辑、字段形状、API 契约、label 键名字符串值。`AgentSessionQuerier` 内部职责拆分属 issue #327，本 issue 不碰。

**安全网**：`Directory.Build.props:8` 全局 `TreatWarningsAsErrors=true`，任何漏改的 namespace 引用都会编译失败；约 29 个 Session spec + `MigratedServicesRegistrationSpecs`（DI 注册检查）兜底行为回归。DI 注册走 Scrutor 按程序集扫描 marker 接口（`IScopedService`/`ISingletonService`，见 `Infrastructure/Hosting/ServiceCollectionExtensions.cs:34`），与 namespace 无关，故 namespace 改写对 DI 透明。

## Goals / Non-Goals

**Goals:**

- 把 6 个读侧文件迁入 `Sessions/` 目录树，使 `Workflow/Services/Sessions/` 目录消失。
- 6 个文件的 `namespace` 声明与目标目录对齐（`Mohist.Server.Sessions` / `Mohist.Server.Sessions.Services`）。
- 切断 `Sessions/` 对 Workflow 目录的反向 `using`（即消除 `AgentSessionQuery.cs:6`）。
- 同步更新全部消费方的 `using`（src + test），无残留旧 namespace 引用。
- 保持编译（`TreatWarningsAsErrors`）与既有 Session/DI spec 全绿。

**Non-Goals:**

- 不拆 `AgentSessionQuerier` 的内部 7 职责（issue #327）。
- 不合并 `Sessions/` 与 `Agent/`（独立子域）。
- 不改任何字符串值、字段形状、API 响应契约。
- 不解决"新增 label 要改三处文件"的协调问题（后续优化）。
- 不消除 `AgentSessionQuerier` 对 `WorkflowQuerier` 的**运行时** DI 依赖（见 Risks）——本 issue 的不变量严格针对 *using 引入语句*，与 spec 第 31 行对全限定名的豁免一致。

## Decisions

### D1：目标 namespace 按目录物理对齐，而非保留旧名

5 个进 `Sessions/Services/` 的文件声明 `namespace Mohist.Server.Sessions.Services`；`AgentSessionReadModels.cs`（DTO）进 `Sessions/` 声明 `namespace Mohist.Server.Sessions`。

**理由**：代码库既有约定就是"namespace 跟目录走"（`Sessions/Services/AgentSessionQuery.cs` 在 `namespace Mohist.Server.Sessions.Services`，`Sessions/Domain/*` 在 `Mohist.Server.Sessions.Domain`）。保留旧名会让目录与 namespace 再次错位，违背本 issue 初衷。

**备选**：只搬目录不改 namespace——被否，物理目录与 namespace 不一致正是当前 bug 的根因。

### D2：`AgentSessionQuerier.cs` 的两条 Workflow using 改全限定名，而非 using-alias

迁入 `Sessions/Services/` 后，该文件现有的两条 `using Mohist.Server.Workflow.*` 会违反 spec 的"`Sessions/` 下不得出现 `using Mohist.Server.Workflow.*`"不变量。处理方式：

- `using Mohist.Server.Workflow.Domain.Run;` —— **直接删除**。该 namespace 唯一引用点是 `AgentSessionQuerier.cs:1550` 的 `Workflow.Domain.Run.TaskRunStatus.Running`，已经写成全限定名，这条 using 实为冗余。
- `using Mohist.Server.Workflow.Services;` —— **删除并把类型引用改为全限定名**。该 namespace 的唯一消费类型是 `WorkflowQuerier`（构造函数注入，见 `AgentSessionQuerier.cs:31,35`，仅 2 处类型名出现）。改写为 `Mohist.Server.Workflow.Services.WorkflowQuerier`。

**理由**：spec 第 31 行明确把全限定名列为豁免项；FQN 让反向依赖在 diff 中"显形"（reviewer 一眼看到 Session 文件引用了 Workflow 类型），而 using 引入把它隐藏。

**备选（被否）**：`using WorkflowQuerier = Mohist.Server.Workflow.Services.WorkflowQuerier;`。技术上不匹配 spec 的 `^using Mohist\.Server\.Workflow\.` 正则，但属于规避不变量的灰色写法，且仍把依赖隐藏在文件头，违背"反向依赖应当可见"的精神。

### D3：`Workflow→Session` 的正向 using 全部保留并改写，不双向都砍

`WorkflowGrain`、`WorkflowSessionHealthService`、`WorkflowActivityQuerier` 等 Workflow 目录下的消费方引用 Session 读模型，迁移后改为 `using Mohist.Server.Sessions.*`。

**理由**：Workflow 消费 Session 读模型是合规正向依赖（叶子域被上游消费），spec 第 41–47 行明确要求保留。本 issue 只砍 Session→Workflow 的反向依赖。

### D4：同步修正一处 doc-cref

`AgentSessionReadModels.cs:248` 的 `<see cref="Workflow.Services.Sessions.AgentSessionQuerier.GetGenericSessionSummaryAsync"/>` 随 namespace 改写为 `Sessions.Services.AgentSessionQuerier...`。

**理由**：保持 doc 注释可解析；虽不阻断编译，但属于本次 namespace 变更的自然组成部分。

### D5：DI 注册无需手动改动

`AgentSessionQuerier` 通过实现 `IScopedService` 被 Scrutor 程序集扫描注册（`ServiceCollectionExtensions.cs:34`）。扫描键是接口 + 程序集，与 namespace 无关。namespace 改写后注册自动正确。

**理由**：避免引入无谓的 DI 改动，把变更面收敛在"文件位置 + namespace + using"三件事。`MigratedServicesRegistrationSpecs` 作为回归兜底。

## Risks / Trade-offs

- **[漏改消费方 using 导致编译失败]** → `TreatWarningsAsErrors=true`（`Directory.Build.props:8`）将其升级为硬错误；CI 必然拦截。这是本 issue 最大的兜底，故风险评级为 low。
- **[残留运行时 Session→Workflow 耦合未消除]** `AgentSessionQuerier` 构造函数注入 `WorkflowQuerier`，这是 *运行时/DI* 层的反向耦合，本 issue 不解决（spec 不变量仅覆盖 *using*，且 Non-Goals 已排除内部职责拆分）。→ 在本 issue 的 commit/PR 描述里显式标注"残留 `AgentSessionQuerier → WorkflowQuerier` 运行时依赖，留待 issue #327"，避免后续误以为叶子域已完全独立。
- **[迁移中 git 历史/blame 中断]** 用 `git mv` 而非 delete+add 保留文件 rename 痕迹。→ 实施时统一用 `git mv`，再编辑 namespace；`git log --follow` 可追溯。
- **[宽泛搜索误伤全限定名]** spec 不变量针对 `^using Mohist\.Server\.Workflow\.` 形式的 *using 语句*，全限定名（如 `Mohist.Server.Workflow.Services.WorkflowQuerier`）是合规的。→ check 阶段的 grep 必须锚定 `^\s*using\s+Mohist\.Server\.Workflow\.`，不要搜裸字符串 `Mohist.Server.Workflow`，否则会误报 D2 故意保留的 FQN。
- **[spec 中"`Sessions/` 无 `using Mohist.Server.Workflow.*`"的判定边界]** spec 第 31 行已豁免全限定名，本设计 D2 据此实施。若 reviewer 认为 FQN 仍算"反向依赖"，则需在 #327 里把 `WorkflowQuerier` 依赖一并拆掉——但那超出本 issue 范围。

## Migration Plan

本变更无运行时迁移、无 schema 变更、无 API 契约变更（namespace 是内部实现细节，非公开 API）。部署即合并 PR。

**实施顺序**（机械、可回滚）：

1. `git mv` 5 个文件到 `Sessions/Services/`，1 个文件（`AgentSessionReadModels.cs`）到 `Sessions/`。
2. 改 6 个文件的 `namespace` 声明（D1）。
3. 删除 `AgentSessionQuerier.cs` 的两条 Workflow using，并把 `WorkflowQuerier` 改全限定名（D2）。
4. 改写 `AgentSessionReadModels.cs:248` 的 cref（D4）。
5. 全仓 `rg -l "Mohist\.Server\.Workflow\.Services\.Sessions"`，逐文件把 `using` 改为对应的 `Mohist.Server.Sessions.*`（src 14 + test 23）。
6. `dotnet build Mohist.sln` —— `TreatWarningsAsErrors` 拦截任何遗漏。
7. `npm test -w packages/server`（或等价 server 测试命令）—— 确认 Session spec + `MigratedServicesRegistrationSpecs` 全绿。
8. 终止条件自检：`ls Workflow/Services/Sessions` 应不存在；`rg "^using\s+Mohist\.Server\.Workflow\." src/Mohist.Server/Sessions/` 应无输出。

**回滚**：纯搬迁 PR，回滚即 `git revert`。无数据/状态副作用。

## Open Questions

- **`AgentSessionQuerier → WorkflowQuerier` 的运行时依赖是否要在本 issue 内一并标注为已知技术债？** 建议：在 PR 描述里显式记录、挂到 issue #327，不在本 issue 内解决（与 spec Non-Goals 一致）。等待 owner 确认。
- **check 阶段的 grep 锚点**：建议 check skill 用 `rg "^\s*using\s+Mohist\.Server\.Workflow\." src/Mohist.Server/Sessions/` 作为"反向 using 清零"的判定命令，避免误报 D2 的全限定名。需与 check stage 对齐该命令。
