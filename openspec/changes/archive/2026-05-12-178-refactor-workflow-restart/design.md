## Context

当前实现把三个不同维度压进了 `Issue.status` 和少量附属字段里：长期生命周期（`active/paused/closed/completed`）、最近一次推进问题（`blocked/interrupted`）、以及用户恢复动作（`reopen/retry/rerun/restart`）。这导致同一个词在不同层含义不同：`reopen` 同时承担 closed issue 重新打开和 paused/interrupted pipeline 恢复，`restart` 则通过重置到 `backlog` 粗暴覆盖多种真实意图。

代码上，这种耦合已经体现在多个入口：

- `IssueService.reopen()` 允许 `closed/blocked/paused/interrupted -> active`
- `POST /api/issues/:number/reopen` 会自动恢复并入队 `resume-pipeline`
- `POST /api/issues/:number/retry` 依赖 `status === blocked`
- `POST /api/issues/:number/restart` 把 issue 重置到 `backlog`
- Web/CLI 把 `blocked` 当成用户可理解的主要状态，并在中断恢复时复用 `reopen`

本次不重做数据库表，也不实现 #176 的 `rewind` 本体。设计目标是在现有存储和 runner 机制上，把用户语义先收敛清楚，让后续 `rewind` 能作为独立动作接入，而不是继续叠加一个含混动词。

## Goals / Non-Goals

**Goals:**

- 让用户可见恢复动词一一对应真实意图：`resume`、`retry`、`rerun`、`reopen`
- 从产品语义上移除 `restart`，并阻止新文案继续推荐它
- 把 `reopen` 收窄为 `closed -> open(active)`，不再承担 pipeline 恢复
- 保留现有 `IssueStatus.Blocked` / `IssueStatus.Interrupted` 作为第一版内部兼容表示，但通过统一派生层把它们解释为 “needs action / interrupted evidence” 而不是长期 lifecycle
- 为 API、CLI、Web 提供同一套恢复决策规则，减少每层各自判断
- 为后续 #176 `rewind` 保留清晰接入点，不再依赖 `restart`

**Non-Goals:**

- 不新增 `currentFailure` 数据表或完整 failure history
- 不在本次实现真正的 `rewind` 命令与后端执行逻辑
- 不重构 agent session 持久化模型；session 仍然是内存态 evidence
- 不移除 `pause/resume` 能力
- 不一次性删除所有内部 `blocked` 字段；重点是先隔离用户语义

## Decisions

### D1: 增加恢复语义派生层，而不是立即替换底层状态枚举

实现上新增一个集中式派生函数，基于 `issue.status`、`issue.stage`、`approvalState`、`mergeState`、`blockedReason`、checkpoint 可用性等信息，生成用户可见的恢复语义对象，例如：

- lifecycle label: `open | paused | closed | completed`
- problem kind: `failed | interrupted | awaiting-approval | none`
- allowed actions: `resume | retry | rerun | reopen | close | rewind`
- display label: `Needs action | Failed | Interrupted | Awaiting approval`

第一版不改 SQLite schema，不新增 `currentFailure` 持久字段。`blocked` 继续作为内部兼容状态，但它不再直接暴露为用户动作模型，而是先经过派生层解释。

这样可以把复杂度下沉到一处，避免 API、CLI、Web 各自复制 “什么时候可以 retry / reopen / rerun” 的判断。

**Alternatives considered:**

- 直接把 `IssueStatus` 改成 `open/paused/closed/completed` 并新增 `currentFailure` 持久字段：长期更干净，但会扩大数据库、repo、测试和历史数据迁移范围，不适合本次
- 继续在各入口点零散修补文案和判断：改动小，但会保留现有语义漂移，后续 `rewind` 仍难接入

### D2: `reopen` 只做 closed issue 的 lifecycle 转换

`IssueService.reopen()` 收窄为只允许 `IssueStatus.Closed -> IssueStatus.Active`。`POST /api/issues/:number/reopen` 只负责这个状态转换，并返回普通 reopen 成功响应；它不再承担恢复 session、自动 enqueue `resume-pipeline`、重置到 draft/backlog、或清理失败上下文。

这项决策把 “重新打开一个不再推进的 issue” 和 “继续一次未完成的尝试” 明确分开。前者是生命周期决策，后者是恢复动作，应由 `resume/retry/rerun` 处理。

**Alternatives considered:**

- 保持 reopen 多态，根据状态自动做 resume/reset：延续现状，但用户仍需要猜 reopen 到底会恢复、重置还是重新打开
- 废弃 reopen，统一用 resume：会损失 closed issue 的自然词汇，不符合产品语义

### D3: 新增显式 `resume` API，承接 paused/interrupted 恢复

保留 `IssueService.resume()` 作为生命周期恢复入口，但收窄它的适用场景：仅用于 `paused` 或 `interrupted` 恢复到 `active`。新增 `POST /api/issues/:number/resume`，由它接管当前 `reopen` 里与 pipeline 恢复有关的逻辑：

- 校验 issue 当前处于 `paused` 或 `interrupted`
- 恢复为 `active`
- 在 agentRunner 可用时入队 `resume-pipeline`
- 不改变 `stage`
- 不清 checkpoint

如果缺少可恢复 session 或运行条件不满足，返回明确错误，提示用户使用 `retry`、`rerun` 或后续 `rewind`，而不是偷偷回滚 stage。

这会与现有 `http-api` / `cli-interface` spec 中 “resume/pause removed” 的历史约束冲突，因此对应 capability 需要更新。

**Alternatives considered:**

- 继续借用 `reopen` 路由：避免加新端点，但语义继续混乱
- 让 `resume` 也支持 blocked：会和 `retry/rerun` 再次重叠

### D4: `retry` 和 `rerun` 基于恢复语义判断，不再直接绑定 `blocked`

`retry` 和 `rerun` 的后端前置条件改为调用共享恢复判定函数，而不是直接检查 `issue.status === blocked`。

具体规则：

- `retry`: 仅当 issue 处于 failed/needs-action 且存在可继续使用的 checkpoint 或明确可重试失败证据时允许；成功后恢复 `active` 并 enqueue `resume-pipeline`
- `rerun`: 仅当当前 stage 可重跑且不在 `draft/backlog/done` 时允许；删除当前 stage checkpoint 与安全的 stage-local artifacts，然后 enqueue `resume-pipeline`
- 两者都不建议也不模拟 `restart`；没有 checkpoint 的 `retry` 不再自动回到 `backlog`，而是返回明确错误，提示使用后续 `rewind --to backlog` 或当前可用动作

这项决策避免了“失败恢复动作里仍内嵌 restart fallback”的旧逻辑。

**Alternatives considered:**

- 让 retry 保留“无 checkpoint 就 reset 到 backlog”：兼容旧行为，但本质仍是隐式 restart
- 仅改文案，不改条件判断：会让前端文案和后端行为继续不一致

### D5: `restart` 变为显式废弃接口，而不是静默保留旧行为

`POST /api/issues/:number/restart` 暂不立即删除路由，先改为稳定的废弃响应：返回 409 或 410，消息明确为 `restart has been removed; use retry, rerun, or rewind instead`。这样可以：

- 及时阻止旧客户端继续触发 destructive reset
- 给现有调用方稳定迁移提示
- 保持回滚成本低于直接删路由

CLI 和 Web 不再暴露任何 Restart 入口，也不在错误提示中提及 restart。

**Alternatives considered:**

- 直接删除路由并返回 404：更彻底，但对现有客户端错误定位较差，用户看不出应该迁移到哪个动作
- 保留旧行为并标记 deprecated：会继续制造不可解释的恢复路径

### D6: UI 采用“生命周期 + 当前问题”双层展示，而不是直接映射 status 枚举

Web 卡片、Issue 详情、CLI show/list 输出都改为展示两层信息：

- 生命周期层：Open / Paused / Closed / Completed
- 当前问题层：Needs action / Failed / Interrupted / Awaiting approval / Integrating

其中：

- `IssueStatus.Blocked` 默认显示为 `Needs action`，在有明确失败原因时可显示 `Failed`
- `IssueStatus.Interrupted` 显示为 `Interrupted`
- `IssueStatus.Closed` 只展示 `Reopen`
- `IssueStatus.Paused` 和 `Interrupted` 展示 `Resume`
- failed/needs-action 展示 `Retry`、`Rerun stage`，以及占位的 `Rewind` 提示或 disabled CTA（若产品希望提前教育用户语义）

实现上优先在 Web/CLI 复用同一份 API 派生字段；如果本次 API 不新增字段，则前端先引入本地 helper，但 helper 的规则必须与后端共享模块保持一致，避免再次分叉。

**Alternatives considered:**

- 继续直接渲染 `IssueStatus`：实现简单，但无法表达 blocked 只是当前问题而不是长期生命周期
- 只改文案，不改动作矩阵：用户仍会在错误状态下看到错误动作

### D7: 恢复能力判断集中在服务层，路由只做编排

为减少 `api/issues.ts` 中重复的 `blocked/interrupted/approval/checkpoint` 分支，本次在服务层增加单一入口，例如 `IssueRecoveryService` 或扩展 `IssueService` 的恢复判定方法，用来返回：

- 当前恢复分类
- 允许的动作集合
- 每个动作的失败原因
- 是否需要 agentRunner / checkpoint / worktree

路由层仅根据该结果选择 HTTP 状态码和消息，并触发对应 enqueue、checkpoint 删除、approval 清理等副作用。

这样可以把“哪些动作允许、为什么不允许”这类最容易漂移的知识放在一个深模块里，而不是散落在 API、CLI、Web 和 runner 中。

**Alternatives considered:**

- 把判断逻辑留在 `api/issues.ts`：最快，但重复分支已经很多，继续加 `resume/retry/rerun/rewind` 会进一步放大复杂度
- 全部塞进 `AgentRunnerService`：会把生命周期决策和运行时编排耦合得更紧

## Risks / Trade-offs

- [内部仍保留 `blocked` / `interrupted` 枚举，语义没有一次性清干净] → 通过集中派生层隔离用户语义，并在 spec/test 中禁止新入口直接面向 `blocked` 做产品判断
- [新增 `resume` 端点会与历史 spec“resume removed”冲突] → 在本 change 的 spec delta 中显式替换旧约束，并用测试锁定新行为
- [旧客户端仍可能调用 `restart`] → 保留废弃路由返回明确迁移错误，而不是 silent 404
- [无 checkpoint 的失败恢复体验会比过去更严格] → 返回明确下一步建议，避免隐式 reset；待 #176 实现后由 `rewind --to backlog` 补齐能力
- [API、CLI、Web 各自实现判断可能再次漂移] → 优先让 API 返回统一 recovery metadata；如果分阶段落地，要求前后端共用同一规则源或镜像测试
- [Integrate/done 阶段恢复语义仍有副作用风险] → 本次 design 明确不把 `restart` 替换成另一个隐式 reset；对高风险阶段仅暴露保守动作，并把 rewind 高风险确认留给后续设计

## Migration Plan

1. 引入恢复语义判定层，并补服务层单元测试，覆盖 `closed/paused/interrupted/blocked` 与不同 stage、approval、mergeState、checkpoint 组合。
2. 收窄 `IssueService.reopen()`，新增或恢复 `POST /api/issues/:number/resume`，更新 `POST /api/issues/:number/reopen` 只处理 closed issue。
3. 改造 `retry`/`rerun`/`restart` 路由：`retry` 不再回退到 backlog，`restart` 返回废弃错误，所有错误提示去掉 restart 建议。
4. 更新 CLI 命令和帮助文案：`reopen` 只对应 closed issue，`resume` 对应 paused/interrupted，移除任何 Restart 文案。
5. 更新 Web issue 卡片和详情页：替换 `Blocked` 用户标签，调整动作矩阵，移除 Restart 入口。
6. 更新 OpenSpec deltas 和测试，确保 API/CLI/Web 恢复动词一致。
7. 发布时兼容旧客户端调用 `restart` 的废弃响应；如需回滚，可临时恢复旧前端入口，但不建议恢复旧 `restart` 语义。

## Open Questions

- API 是否在本次直接返回统一的 recovery metadata（例如 `availableActions`、`displayStatus`、`problemKind`），还是先只改行为与文案？直接返回 metadata 更利于前后端一致，但会扩大响应面。
- `paused` 是用户主动暂停，`interrupted` 是系统中断；两者在 Web 上是否都统一显示为 `Resume`，还是保留不同说明文案但共享动作？
- 对 `blocked` 的用户标签，本次默认使用 `Needs action` 还是按失败原因细分为 `Failed` / `Needs action`？前者更稳，后者更有信息量。
- 当 issue 处于 failed 状态但没有 checkpoint 时，`retry` 应返回 409 并建议 future `rewind`，还是暂时允许 `start` 从 backlog 重新发起？本设计倾向前者，以避免隐式 restart 复活。
