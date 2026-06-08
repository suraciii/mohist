# Mohist 2026-06-08 全项目审计汇总

> **6 个并行审计 agent 输出 232 个发现：30 P0 + 67 P1 + 80 P2 + 55 P3。**
> 4 个 P0 是跨多个审计 agent 报告的**系统性问题**，不是单一视角的误报。

| 审计 | 范围 | P0 | P1 | P2 | P3 | 文件 |
|---|---|---:|---:|---:|---:|---|
| Workflow domain | WorkflowGrain + 状态机 + lease | 4 | 8 | 13 | 10 | `workflow-domain-audit.md` |
| Issue domain | IssueGrain + bus subscription | 4 | 7 | 9 | 8 | `issue-domain-audit.md` |
| Event bus | IEventBus + SignalR + EventCatalog | 5 | 8 | 11 | 4 | `event-bus-audit.md` |
| Architecture | Bounded Context + 持久化 + DI | 10 | 26 | 21 | 6 | `architecture-audit.md` |
| Code quality | 死代码 + 命名 + 错误处理 | 6 | 11 | 18 | 22 | `code-quality-audit.md` |
| Runner + Web | TS runner + React Web | 3 | 7 | 8 | 5 | `runner-web-audit.md` |
| **总计** | | **30** | **67** | **80** | **55** | |

## 跨审计 P0（系统性问题，4 个主题）

### 主题 A — Reverse-DNS 事件不可见 / 缺 `projectid` 扩展（5 个 P0）

| 来源 | P0 | 一句话 |
|---|---|---|
| Workflow | P0-3 | 17 个 reverse-DNS workflow 事件（`TaskCompleted` 等）从未被 `EventBridge` 转发到 Web |
| Workflow | P0-4 | `lease_expired` 和所有 reverse-DNS 事件都缺 `projectid` 扩展 |
| Issue | P0-1 | `WorktreeCleanupService` 因为缺 `projectid`/`issueno` 而**是死代码**（worktree 永远不被清理）|
| Event bus | P0-3 | `EventBridge` 启动订阅时只迭代 `EventCatalog.All`（56 个 legacy），0 个 reverse-DNS |
| Architecture | 1.2 | `ProjectGrain` 直接 mutation `ProjectWorkflowProfiles` 表（跨 BC 边界）|

**根因**：`WorkflowRunStore.Publish` 和 `EventStore.AppendWorkflowEventAsync` 都调 `CloudEventFactory.Create(..., workflowRunId: runId)`，**没有**传 `projectId` 或 `issueNumber`。`CloudEventFactory` 的 `IProjectScoped` 自动 lift 也失败（payload 是 `JsonElement` 结构体，不能 `is` 检查接口）。`EventBridge` 又只订阅 legacy names。

**修一处，全部受益**：
1. `CloudEventFactory.Create` 增加 `projectId` / `issueNumber` 参数（已支持，但未在调用方传）
2. `WorkflowRunStore.Publish` 在 emit 前从 row 读 `Metadata.Annotations["projectId"]` 和 `IssueId`
3. `EventCatalog.All` 改为 `Legacy + ReverseDns` 的并集（或 `EventBridge` 同时迭代两个）
4. `EventBridge.ExtractProjectId` 正确填充 `projectid` 扩展

**用户可观测影响**：Web UI 看不到 per-task / per-check / per-stage 实时事件，UI 卡在粗粒度 `stage_changed`；worktree 永远不清理（磁盘泄漏）；`project:global` 群发（跨租户泄漏）。

---

### 主题 B — Orleans 持久化 / 状态机事务（4 个 P0）

| 来源 | P0 | 一句话 |
|---|---|---|
| Architecture | 2.1 | `IStateStore<T>` 接口在 5/8 实现中**行为不匹配**（`ListAsync` 抛 `NotSupported` 而非 `NotImplemented`，签名漂移）|
| Architecture | 2.2 | `WorkflowGrain` lease + variables 写入**不在同一 DB 事务**（崩溃后 state 不一致）|
| Architecture | 2.3 | `IWorkflowRunStore.SaveAsync` rollback 路径是**死代码**（`using` 已自动 rollback）|
| Architecture | 2.4 | `IssueGrain` 事件 handler **离线 grain 线程**改状态（违反 Orleans actor 模型）|

**根因**：自建 `IStateStore<T>` 抽象 + 手动 JSON column + 手卷 ETag 这条路径绕过了 EF Core 的 transaction / concurrency 原语。

**修一处决策**：是统一回 `IPersistentState<T>`（Orleans 原生），还是补齐 `IStateStore<T>` 的事务语义？前者工作量小、风险低、3 个 audit 报告的并发问题一并解决。

---

### 主题 C — 关闭期 / 激活期事件丢失（4 个 P0 涉及）

| 来源 | P0 | 一句话 |
|---|---|---|
| Workflow | P0-1 | `CheckLeaseAgeAsync` emit `lease_expired` 是 **fire-and-forget**；lease 不清，workflow 不恢复。docstring 承诺的"re-dispatch / fail transition" 都没实现 |
| Architecture | 5.1 | 4 个 hosted services 中 **3 个** `StopAsync` 是空方法（关闭期未 flush 状态）|
| Event bus | P0-5 | **grain 转换期 lost events 未处理**（subscription 注销到下一次 activate 之间的窗口）|
| Code quality | P1-1 | `OnDeactivateAsync` 吞所有异常 + 静默丢失内存状态 |

**根因**：Orleans activation 周期（activate/deactivate/migrate）和 IEventBus 订阅生命周期没有配对。

**修法**：
- 主题 A 的修法自动让 `lease_expired` 被 `IssueGrain` 看到（它已经订阅了 `WorkflowRunFailed` 类似的 type），但**主状态机**仍需 `CheckLeaseAgeAsync` 同步恢复
- 4 个 hosted service 统一 `StopAsync` 模板：取消 token、await in-flight、dispose subscriptions
- `OnDeactivateAsync` 用 `try { ... } catch (Exception ex) { _log.LogError(ex, ...); }` 替代 `catch { }`

---

### 主题 D — 异常处理 / 状态机正确性（3 个 P0 + 1 个 P0）

| 来源 | P0 | 一句话 |
|---|---|---|
| Issue | P0-2 | `ExceptionMiddleware` 把**所有** `InvalidOperationException` 映射到 404（"not found"是错的，应该是 "conflict"）|
| Issue | P0-3 | `/close` / `/unarchive` 在 Done issue 上返回 404 而非 409（用 `catch(InvalidOp) → NotFound`）|
| Architecture | 6.1 | `ExceptionMiddleware` 只处理两种异常类型，其他都是 500 |
| Architecture | 6.2 | `IssueGrain.StartWorkAsync` **对所有错误**抛 `InvalidOperationException` |

**根因**：缺少 `DomainNotFoundException` / `DomainConflictException` 区分；中间件"图省事"把 `InvalidOperationException` 当 404；`/close` 等路由局部 `catch` 又复制了这个错误模式。

**修法（1 处）**：
1. 新建 `Infrastructure/Errors/`：`DomainNotFoundException` / `DomainConflictException` / `DomainValidationException`
2. Issue / Project 域 throw 站点全部迁移
3. `ExceptionMiddleware` 三 catch 分支：404 / 409 / 400
4. 局部路由的 `try/catch` 删掉（中间件已统一）

---

### 主题 E — Fire-and-forget 异常（3 个 audit 都报告）

| 来源 | P0 | 一句话 |
|---|---|---|
| Issue | P0-4 | `IssueGrain` 三个 bus handler 的 `_ = CompleteWorkAsync(...)` 异常**完全无观察** |
| Code quality | P0-6 | `IssueGrain` 事件 handler 中 `_ = Method()` 异常未观察 |
| Code quality | P0-2, P0-3 | `async void` 事件 handler **无顶层 try/catch**（2 处）|
| Event bus | P0-4 | 同步派发阻塞 emitting grain（fire-and-forget 是症状而非根因）|
| Event bus | P0-5 | grain 转换期 lost events |

**根因**：之前我决定"bus 同步派发，handler 内部 fire-and-forget 跨 grain"的简化模型——当 handler 内的 grain method 抛异常时，无人观察，Orleans 默认不 surface。

**修法（2 处）**：
1. **`OnType` 签名升级为 `Func<CloudEvent, Task>`**：handler 内部 `await` 跨 grain 调用，Orleans 排进 inbox，异常被 grain 的 caller 链路看到
2. **`fire-and-forget` 必须 `ContinueWith(OnlyOnFaulted → LogError)`**（即便签名升级，作为保险仍需要）
3. 删除所有 `async void` 事件 handler

这正是用户上轮"eventbus 同步性分析"我提议过的方案——现在被 3 个独立 audit 验证为必要。

---

### 主题 F — 死代码 / 抽象泄漏（3 个 P0）

| 来源 | P0 | 一句话 |
|---|---|---|
| Code quality | P0-1 | `EventBusEventTypes.cs` 已废弃，superseded by `EventCatalog`，但仍被引用 |
| Code quality | P0-4 | `DispatchLifecycleHooksAsync<T>` 是 no-op shim，**注释自承**（Step 8 的"妥协品"）|
| Code quality | P0-5 | `GetHookContext` 是死代码（`WorkflowGrain` 1153 行里残留）|

**修法**：3 个文件删除 + 验证 build + 验证测试。

---

## 单审计 P0（按优先级排）

| # | 主题 | 来源 | 一句话 |
|---|---|---|---|
| 1 | Approval 状态机 | Workflow P0-2 | Reject 走 `Rerun` 重跑当前 stage，**无 spec**，拒绝原因在新 stage 上丢失 |
| 2 | 跨 BC 边界 | Architecture 1.2 | `ProjectGrain` 直接 mutation `ProjectWorkflowProfiles` 表 |
| 3 | 配置管理 | Architecture 4.1 | 重复的 `StripJsoncComments` 实现（2 份）|
| 4 | 配置管理 | Architecture 4.2 | `AddMohistConfigFile` 参数叫 `reloadOnChange` 但**不热重载** |
| 5 | DB migration | Architecture 9.1 | 单一 `InitialSchema` 覆盖所有 schema（无法演进）|
| 6 | Migration | Architecture 2.x | 见主题 B |
| 7 | Web envelope | Runner P0-1 | `rebase_started` / `rebase_progress` 在 Web 丢失（用户可见回归 1 周）|
| 8 | Web envelope | Runner P0-2 | `unwrapEnvelope` 鸭式类型（`'payload' in rawData`）脆弱 |
| 9 | Runner 关闭 | Runner P0-3 | `executeAndReport` 关闭时无法报告（lease 5 min 后超时重排）|

---

## P1 速览（按修复 ROI 排）

### 极高 ROI（改 1 文件修多处）
- **Workflow P1-1** + **Event bus P0-5** + **Code quality P1-1**：合并到主题 C 的修法
- **Event bus P0-4** + **Issue P0-4** + **Code quality P0-6**：合并到主题 E 的修法
- **Event bus P1-1** + **P1-2** + **P1-6**：reverse-DNS 5 个无 producer + 26 个无 subscriber = 把 EventCatalog 砍半
- **Architecture 4.5, 4.6**：`Environment.Get*` / `File.ReadAllText` 偷绕过 `EnvironmentAbstractions` / `IFileSystem`（BannedApiAnalyzer 应能抓，需要核查）
- **Architecture 5.3**：`async void` event handlers

### 高 ROI（单点但常见路径）
- **Issue P1-5**：bus handler 在 bus dispatcher 线程**读** `_issue`（在 grain 线程**改**）—— 重新组织为 await 跨 grain
- **Issue P1-7**：`IssueWorkflowReconciliationService` 无法处理 10K+ stuck issues（每 24h 一次批 500）
- **Issue P1-10**：worst-case latency 24h（daily sweep）
- **Architecture 2.6**：`IStateStore` Load/Save/Delete/List 不接 `CancellationToken`
- **Architecture 2.8**：ETag 手卷实现非 portable
- **Architecture 3.1**：`MohistSiloRegistration` 只支持 single-silo localhost
- **Architecture 6.4**：API 路由不接 `CancellationToken`
- **Code quality P1-2**：`WorkflowRun.Metadata.CreatedAt` = `DateTimeOffset.MinValue` 在 API 暴露为 `0001-01-01`
- **Code quality P1-3**：legacy `type:` 字符串绕过 EventCatalog
- **Code quality P1-4**：agent runtime event-type magic strings 跨 server + web 重复 75 次

### 中 ROI（设计债务）
- **Workflow P1-5** + **Architecture 2.8**：ETag 乐观锁是 no-op / 非 portable
- **Workflow P1-3**：`AddRuntimeTask` 静默取消 pending approval
- **Architecture 2.7**：`IssueCounterStore` 无并发控制
- **Architecture 2.10**：`IEventBus` Singleton + `IssueGrain` `OnActivateAsync` 订阅 = 重新订阅 race
- **Architecture 2.11**：`IStateStore<T>` 注册为 Scoped 但被 Singleton grain 消费
- **Code quality P1-6**：`catch (Exception ex) { _ = ex; }` 吞异常

---

## 修复路线图建议

### Batch 4（5-7 天）— P0 主题 A-E
1. **主题 E**：`IEventBus.OnType` 改 `Func<CloudEvent, Task>`，4 个 caller 改 `await`，IssueGrain handler 改 `await`。`fire-and-forget` 加 `ContinueWith(OnlyOnFaulted)` 保险。删 `async void`。
2. **主题 A**：4 处 emit site 传 `projectId` + `issueNumber`，`EventBridge` 订阅 `EventCatalog.ReverseDns.*`，`EventCatalog.All` = Legacy ∪ ReverseDNS。
3. **主题 D**：新建 `Domain*Exception` 三件套，迁移 5+ throw site，ExceptionMiddleware 改 3 分支，删 5+ 局部 `try/catch`。
4. **主题 C**：`CheckLeaseAgeAsync` 同步恢复（清 lease / `WorkflowRunFailed(LeaseExpired)` / re-dispatch），4 个 hosted service `StopAsync` 模板化，`OnDeactivateAsync` 改 `try/catch+log`。
5. **主题 B**：`IStateStore<T>` 决策（保留 vs 回 `IPersistentState<T>`）—— **强烈建议回退** 到 Orleans 原生。`WorkflowGrain` lease+vars 同事务。
6. **主题 F**：删 3 个死代码文件。
7. **单审计 P0 #1-9**：见上表，逐项修。

### Batch 5（3-5 天）— P1 极/高 ROI
- `EnvironmentAbstractions` 偷绕过的 5-8 处全部收口
- API 路由加 `CancellationToken`
- `IStateStore` 全部加 CT 参数
- 11 个 race condition P1（见 P1 速览）

### Batch 6（2-3 天）— P1 中 ROI + P2 系统性
- Web `LiveTaskProvider` 测试覆盖空白
- Magic strings 75 处 → EventCatalog 统一
- WorkflowGrain 拆类（1153 行）
- 默认 workflow profile 的 self-review 耦合修复

### 不修（确认 P3 接受）
- 命名风格 nitpick（`is null` vs `=== null`）
- 文件组织（待大重构时一并）
- ETag bump 浪费（移到 Batch 6+）

---

## Executive Summary

**生产就绪度：约 70%。**

整个事件机制设计是对的（CloudEvents 1.0.2 envelope、bus-driven Issue ← Workflow、hosted services 解耦），但**实现层有 4 个系统性盲点**（主题 A-E），3 个是**可观测的用户痛点**（Web envelope 丢失事件、Web runner 关闭报告失败、UI rebase 状态卡死），2 个是**潜在数据正确性问题**（持久化事务、grain 状态机事件丢失）。

**Top 3 风险**：
1. **跨租户事件泄漏**：reverse-DNS 事件因缺 `projectid` 落到 `project:global` 群发（主题 A）
2. **Worktree 磁盘泄漏**：`WorktreeCleanupService` 是死代码（主题 A）
3. **Lease 超时无恢复**：`CheckLeaseAgeAsync` 承诺的恢复路径没实现（主题 C）

**Top 3 快速胜利**（单文件 < 100 行）：
1. `IEventBus.OnType` 改 `Func<CloudEvent, Task>` 签名（用户上一轮已分析过的方案）
2. `ExceptionMiddleware` 三分支
3. 删 3 个死代码文件

**Top 3 决策**（需要 owner 拍板）：
1. `IStateStore<T>` 抽象的存废（建议回退 `IPersistentState<T>`）
2. `WorkflowGrain` 拆类时机（>1000 行）
3. 单一 `InitialSchema` migration 演进策略

---

## 审计交叉点图

```
                          ┌─────────────────────────────────────────┐
                          │   30 P0 Findings                       │
                          └─────────────────────────────────────────┘
                                              │
                ┌─────────────────┬───────────┼────────────┬────────────────────┐
                │                 │           │            │                    │
         主题 A (5)         主题 B (4)   主题 C (4)    主题 D (4)         主题 E (3)
         事件可见性         持久化       转换期        异常映射          fire-and-forget
                │                 │           │            │                    │
                ▼                 ▼           ▼            ▼                    ▼
        CloudEventFactory  IStateStore  HostedService  Domain*Exception   IEventBus.OnType
        EventBridge        WorkflowGrain OnDeactivate  ExceptionMiddleware  async → await
        WorktreeCleanup    IssueGrain   CheckLeaseAge IssueRoutes         ContinueWith
        ProjectGrain/BC    ETag         lease_expired WorkflowGrain.throws IssueGrain handlers
```

```
         主题 F (3)       单审计 P0 (9)        P1 高 ROI (15)
         死代码           Approval / BC /       race / CT / magic string /
         抽象泄漏         Config / Web / Runner schema / ETag / Scoped
                │                 │                    │
                ▼                 ▼                    ▼
        EventBusEventTypes  WorkflowGrain.Reject   IEventBus / IStateStore
        DispatchLifecycle   ProjectGrain           CancellationToken
        GetHookContext      StripJsoncComments     11 个具体 race
                            AddMohistConfigFile
                            rebase events
                            unwrapEnvelope
                            executeAndReport
```
