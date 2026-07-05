## Context

`packages/runner/src/server/runner-signalr.ts`（850 行 / scc Complexity 203）是 runner 的传输 hot path：review API 的 git 查询、followup、cancel、workflow-status 推送、连接生命周期全部流经它。当前它把四类互不相关的关注点堆在一个文件里：

1. **连接生命周期** —— hub URL 构造、`withAutomaticReconnect([0,2000,5000,10000,30000])`、`probeLiveness`、`forceReconnect`、`notifyReconnected`、`onreconnected` 回调。
2. **5 个 git workspace 查询 handler** —— `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetWorkspaceStatus`/`GetFileContent`，外加 `isGitWorkTree` 探测与 5 个 git 输出解析器（`parseDiffFiles`/`splitDiffByFile`/`parseCommits`/`parseAheadBehind`/`parseNumstatTotal`）。
3. **推送类 handler** —— `ReceiveFollowup`（fire-and-forget）、`CancelAgentSession`（带 `{ state }` 回复）、`ReceiveWorkflowRunStatus`（registry `active→eligible` 幂等迁移）、`RemoveWorkspace`（runner-root 包含 + registry 一致）。
4. **死代码** —— `normalizeMaterializePayload` 及其 6 个 helper（`parseSetVars`/`parseOutputs`/`parseJsonObject`/`readString`/`readNullableString`/`readNullableNumber`，约 75 行），`src` 内零调用方。

两处遗留债务缠在这个文件上：

- **循环 import workaround**：`runtime/workspace-registry.ts:271-279` 重复实现了一份 `isPathUnder`，注释说明从 `runner-signalr.ts` 导入 `isUnderRunnerRoot` 会在模块加载期成环（runner-signalr 反向 import `WorkspaceRegistry` 类型）。
- **跨层反向 import**：`runtime/cleanup-loop.ts:1` 从 `server/runner-signalr.ts` 导入 `isUnderRunnerRoot`（runtime→server，与既定的 server→runtime 方向相反）。

**测试覆盖现状**（已核实）：`runner-signalr.spec.ts`（1241 行）已覆盖 liveness、followup、cancel、`GetWorkspaceStatus`、`RemoveWorkspace`；`runner-signalr-workflow-status.spec.ts` 覆盖 workflow-status；`workspace-registry-integration.spec.ts` 覆盖 registry。**缺口**：`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 四个 handler 与全部 5 个解析器**零直接覆盖**（解析器甚至未 export）。这是本次 medium 风险评级的核心原因，故 acceptance criteria 强制测试先行。

**利益相关方**：runner 维护者（可读性、可测性）；server 端 review/cancel/followup 路由（依赖 handler 返回形状与 cancel 回复形状）；web/cli（零改动，纯内部重构）。

## Goals / Non-Goals

**Goals:**

- 把 `runner-signalr.ts` 从 850 行收敛为"组装 + 委托"：URL/reconnect 构造、`register*Handlers` 调用、liveness 委托。
- 按关注点拆出独立模块：git 解析器、workspace 路径解析、session-target 解析、liveness probe、5 个 handler 簇（git 查询 / workspace 移除 / followup / cancel / workflow-status）。
- 消解 `workspace-registry.ts` 的 `isPathUnder` 循环 import workaround 与 `cleanup-loop.ts` 的跨层反向 import。
- 删除 `normalizeMaterializePayload` 死代码。
- **测试先行**：在迁移任何 git handler 代码之前，先为 `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 及解析器补齐直接测试。

**Non-Goals:**

- 不改任何 SignalR 线上契约：方法名、handler 返回形状、cancel 回复形状（`{ state }` 镜像进 HTTP）、hub URL、重连间隔序列、followup 的 fire-and-forget 语义、legacy 顶层字段回退——全部字节级不变。
- 不改 `connection.ts`。server/web/cli 零改动。
- 不做性能优化、不加新依赖、不做数据迁移。

## Decisions

### D1. 分阶段、低风险先行，测试卡在 git handler 迁移之前

七个阶段，每阶段独立可 review、可回滚，且每阶段后 `npm test -w packages/runner` 必须全绿：

| 阶段 | 内容 | 风险 | 为何此顺序 |
|------|------|------|-----------|
| P0 | 删 `normalizeMaterializePayload` + 6 helper（约 75 行） | 零 | 无调用方，先清场 |
| P1 | 抽纯解析器到 `git-parsers.ts` + 补 `git-parsers.spec.ts` | 零 | 纯函数，无行为变化；为 P5/P6 测试铺路 |
| P2 | 抽 `workspace-query.ts`（`resolveWorkspaceQuery`/`isUnderRunnerRoot`/`WorkspaceQuery`），更新 cleanup-loop / workspace-registry 导入，删 `isPathUnder` | 低 | 消解循环 import + 跨层 import；为 P6 的 git handler deps 铺路 |
| P3 | 抽 `session-target.ts`（`resolveSessionTarget` + 相关类型） | 低 | 纯函数；followup/cancel handler 依赖它 |
| P4 | 抽 `liveness-probe.ts`（`probeLiveness`/`forceReconnect`/`notifyReconnected`） | 低 | 操作 connection，已有间接覆盖，补直接测试 |
| **P5** | **测试先行补缺口**：在 `runner-signalr.spec.ts` 内为 `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 经现有 `findHandler` + `setRunnerSignalRGitRunnerForTest` 接缝补齐全部 spec 场景 | 零（只加测试） | **acceptance 强制**：迁代码前先用测试钉住当前行为 |
| P6 | 抽 git handler 到 `workspace-git-handlers.ts` 为 `registerWorkspaceGitHandlers(conn, deps)`；P5 测试继续绿 | 中 | hot path，但有 P5 钉死 |
| P7 | 抽其余 4 个 handler 簇（`RemoveWorkspace`/`ReceiveFollowup`/`CancelAgentSession`/`ReceiveWorkflowRunStatus`）各自成 `register*Handlers` | 中 | 已有覆盖，行为不变 |

**备选**：一次性大爆炸拆分。**否决**——hot path 上不可一次性改 850 行，且无法满足"测试先行"硬约束。

### D2. 模块放置：路径解析下沉到 `runtime/`，handler 簇留在 `server/`

```
packages/runner/src/
  runtime/
    workspace-query.ts      ← 新：WorkspaceQuery, resolveWorkspaceQuery, isUnderRunnerRoot
  server/
    git-parsers.ts          ← 新：parseDiffFiles/splitDiffByFile/parseCommits/parseAheadBehind/parseNumstatTotal
    session-target.ts       ← 新：resolveSessionTarget + ReceiveFollowup*/CancelAgent* 类型
    liveness-probe.ts       ← 新：probeLiveness/forceReconnect/notifyReconnected
    workspace-git-handlers.ts        ← 新：registerWorkspaceGitHandlers + isGitWorkTree + git() helper
    workspace-removal-handler.ts     ← 新：registerWorkspaceRemovalHandler
    followup-handler.ts              ← 新：registerFollowupHandler
    cancel-handler.ts                ← 新：registerCancelHandler
    workflow-run-status-handler.ts   ← 新：registerWorkflowRunStatusHandler
    runner-signalr.ts       ← 收敛为：URL/reconnect 构造 + register* 调用 + liveness 委托
```

**为何 `workspace-query.ts` 放 `runtime/` 而非 `server/`**：`isUnderRunnerRoot` 的两个消费者是 `runtime/cleanup-loop.ts` 与 `runtime/workspace-registry.ts`（同层），而 `server/runner-signalr.ts` 是第三个消费者。放 `runtime/` 使 cleanup-loop 的导入变成同层（消解跨层反向 import runtime→server），而 runner-signalr→runtime 是既定方向（runner-signalr 已 import `WorkspaceRegistry` 类型）。若放 `server/`，cleanup-loop 的跨层反向 import 仍在。

**备选**：把 `isUnderRunnerRoot` 单独放 `system/` 或新 `shared/` 层。**否决**——过度工程，runner 没有其它 `shared/` 先例，且路径解析在语义上属 workspace（runtime）关注点。

**为何 handler 簇留 `server/`**：它们注册 `connection.on(...)`，是 SignalR 传输层的事，与 runner-signalr 同位。

### D3. handler 簇为 `register*Handlers(connection, deps)` 自由函数，deps 按簇最小化

每个簇是一个自由函数，只声明自己需要的 deps：

```ts
// workspace-git-handlers.ts
export interface WorkspaceGitHandlerDeps {
  resolveQuery: typeof resolveWorkspaceQuery
  runCommand: typeof defaultRunCommand      // 注入点，便于测试
  pathExists: typeof defaultExistsSync
}
export function registerWorkspaceGitHandlers(
  conn: signalR.HubConnection,
  deps: WorkspaceGitHandlerDeps,
): void { /* conn.on("GetDiff", ...), conn.on("GetCommits", ...), ... */ }

// followup-handler.ts
export interface FollowupHandlerDeps {
  serverConnection: ServerConnection | null
  followupTargetResolver: FollowupTargetResolver | null
}
export function registerFollowupHandler(conn, deps: FollowupHandlerDeps): void
```

`RunnerSignalRClient` 构造函数从 ctor 参数组装各簇 deps 并逐个调用 `register*Handlers(this.connection, deps)`。

**为何自由函数 + 显式 deps 而非类方法**：方法是"测试先行"的障碍——直接测 handler 必须先 `new RunnerSignalRClient(...)` 走完整构造（建连、URL、reconnect）。自由函数让 P5/P6 的直接测试可构造一个仅 `on()` 被 mock 的 fake connection + 自定义 deps 即可捕获 handler 并断言其行为。

**备选 A**：所有 handler 共享一个 `HandlerDeps` 大包。**否决**——簇间耦合，改一簇的 deps 影响全部。
**备选 B**：保留为类方法。**否选**——见上，阻碍直接测试。

### D4. 测试注入接缝：可变绑定随 `git()` helper 落到 `workspace-git-handlers.ts`，runner-signalr 再导出

现有 `setRunnerSignalRGitRunnerForTest` / `setRunnerSignalRExistsCheckerForTest` 是 `runner-signalr.ts` 内的模块级 `let` 可变绑定，P6 时 `git()` helper 与 `pathExists` 绑定迁到 `workspace-git-handlers.ts`。处理方式：

- setter 的**权威定义**随 `git()` helper 落到 `workspace-git-handlers.ts`（绑定在哪，setter 在哪）。
- `runner-signalr.ts` **再导出**这两个 setter 及 `resolveWorkspaceQuery`/`isUnderRunnerRoot`/`resolveSessionTarget`/相关类型，使现有 `from "../src/server/runner-signalr.js"` 的测试导入**零改动**继续工作（最大化遵守"断言不变"，最小化对 1241 行测试文件的触碰）。
- **新增**的直接单元测试（`git-parsers.spec.ts`、`workspace-git-handlers.spec.ts` 等）直接从归属模块导入。

**备选**：不再导出，强制更新所有测试导入路径。**否决**——proposal 虽允许更新导入，但再导出是零成本保险，能在 hot path 上把"纯重排"的 diff 面积压到最小，review 风险更低。

### D5. 契约边界钉死清单（迁移前后逐项核对）

迁移每个 handler 时，下列不变项必须字节级保持，并由测试逐条断言：

- SignalR 方法名：`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetWorkspaceStatus`/`GetFileContent`/`RemoveWorkspace`/`ReceiveFollowup`/`CancelAgentSession`/`ReceiveWorkflowRunStatus`。
- 各 handler 返回形状（见 specs）：`GetDiff` 的 `{base,head,mergeBase,ahead,behind,commitCount,totalAdditions,totalDeletions,files}`、`GetCommits` 的 `{...filesChanged...}`、`GetFileContent` 的 `{base,head}` 独立解析、unresolvable 的 not-found sentinel（`null`/`{exists:false}`/`{base:null,head:null}`）。
- `CancelAgentSession` 回复 `{ state: "cancelled" | "not-cancellable" }`（镜像进 HTTP）。
- hub URL：`{baseUrl 去尾斜杠}/hubs/runner?runnerId=&buildGitHash=`（`buildGitHash` 为 null 时整个参数省略）。
- 重连：`withAutomaticReconnect([0,2000,5000,10000,30000])` 序列不变。
- `ReceiveFollowup` 的 fire-and-forget：不 await `connection.prompt`、runtime event emit 失败不阻塞 prompt、resolver 抛错/返回 null 静默丢弃、不向 transport 抛。
- legacy 顶层 `workflowRunId`/`sessionName` 回退（无 `target` 字段时走 workflow 路径，`projectId: ""`）。
- `probeLiveness` 的 settle-once 幂等、abort-at-call-time 立即返回 false、非 Connected 不调 Ping。
- `forceReconnect` 的 stop→start 顺序、stop 异常吞咽、Disconnected 直 start、stop 后 abort 短路 start。

### D6. `git()` helper 的归属与 AbortController

`git()` helper（包 `runGitCommand("git", args, cwd, signal)`）随 git handler 落到 `workspace-git-handlers.ts`。各 handler 内部 `new AbortController()` 的局部模式保持不变（当前每个 handler 自建一个未触发的 AC 仅作 signal 透传，行为上等价于不取消；保留以最小化行为差异）。`isGitWorkTree`（用 `pathExists` 绑定）同模块。

## Risks / Trade-offs

- **[hot path 行为回归] → 测试先行 + 契约清单钉死**：P5 在迁代码前先用测试覆盖 `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 全部 spec 场景；P6 迁移后同一套测试继续跑；D5 清单逐项核对。
- **[fire-and-forget / 非 await 语义在抽离时被意外改成 await] → 显式断言**：followup handler 测试断言"handler 返回时 `connection.prompt` 尚未 resolve"（用 never-resolving mock + 断言 handler 已返回）；cancel handler 测试断言 `{ state }` 回复不被 cancel 失败污染。
- **[循环 import 在新结构下复现] → 放置已验证**：D2 把 `workspace-query.ts` 放 `runtime/`，三向导入方向（cleanup-loop 同层、workspace-registry 同层、runner-signalr 沿既定 server→runtime）均不成环；P2 完成后跑一次全量 typecheck + test 确认无加载期 cycle。
- **[可变 setter 绑定跨模块后测试隔离泄漏] → setter 随 helper 同模块 + afterEach 复位**：现有测试已在 `afterEach` 调 `setRunnerSignalRGitRunnerForTest(null)`；再导出保持同名，复位语义不变。
- **[大测试文件的渐进式更新引入遗漏] → 每阶段独立全绿**：P0–P7 每阶段后 `npm run typecheck && npm test -w packages/runner` 必须全绿才进下一阶段，禁止跨阶段攒改动。

## Migration Plan

纯内部重构，**无数据/格式/契约变化、无新依赖、server/web/cli 零改动**。

**部署**：
1. 合并 PR 后重建 runner（`mo update runner`）。
2. server 侧无感知（hub 方法名与返回形状未变）。
3. 无需协调 web/cli 发版。

**验证**：
- `npm run typecheck -w packages/runner` 全绿。
- `npm test -w packages/runner` 全绿（含 P5 新增的 git handler/parser 直接测试）。
- 人工抽检：runner 启动后 `getConnectionId()` 非 null、一次 followup 与一次 cancel 端到端走通（依赖现有 e2e 或手动）。

**回滚**：纯 revert PR，无状态需修复。无向前/向后兼容窗口（runner 是唯一消费者，server 契约未动）。

## Open Questions

- **P5 测试的接缝选择**：`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 的"测试先行"经 `RunnerSignalRClient` + `findHandler` 间接接缝（与现有 `GetWorkspaceStatus` 测试同款），还是先把 handler 抽出再直接测？当前倾向前者（迁代码前就要有测试，只能用现有接缝）；P6 抽出后再补直接单元测试作为 P6 的一部分。需在 P5 落地时确认间接接缝能覆盖所有 spec 场景（尤其"head ref 缺失返回 null 不发 diff 查询"这类短路）。
- **`workspace-query.ts` 内 `WorkspaceQuery` 输入类型的位置**：它是 server 推来的 wire 形状，但解析逻辑属 runtime。倾向把类型与解析函数同放 `runtime/workspace-query.ts`（内聚）；若 review 时认为 wire 类型应归 server，可拆为类型在 server、实现在 runtime，但会重新引入跨层。默认不拆。
