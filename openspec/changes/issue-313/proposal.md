## Why

`packages/runner/src/server/runner-signalr.ts`（scc Complexity 203 / 850 行）是 runner 的传输 hot path，但它把四类毫不相关的关注点堆在一个文件里：连接生命周期（建连/重连/存活探测）、5 个 git workspace 查询 handler、followup/cancel/workflow-status 推送、git 输出解析。更糟的是其中约 195 行 git handler / 解析器目前零直接测试覆盖，且文件里还残留一整套无任何 `src` 调用方的 `normalizeMaterializePayload` 死代码（约 75 行）。传输层是 review API、followup、cancel 全部流经的路径，任何一处回归都会断掉 server↔runner 通信；现在拆是因为 pure 重排、不改任何线上契约、调用点全部可见，风险可控，且越拖死代码与未覆盖 hot path 的债务越贵。

## What Changes

- 删除无任何调用方的 `normalizeMaterializePayload` 及其 helper 死代码（`parseSetVars`/`parseOutputs`/`parseJsonObject`/`readString`/`readNullableString`/`readNullableNumber`，约 75 行）。
- git 输出解析器（`parseDiffFiles`/`splitDiffByFile`/`parseCommits`/`parseAheadBehind`/`parseNumstatTotal`）提取为独立模块，使其可直接单测。
- workspace 路径解析（`resolveWorkspaceQuery`/`isUnderRunnerRoot`/`WorkspaceQuery` 类型）提取到独立模块；顺带消解 `workspace-registry.ts` 的 `isPathUnder` 循环 import workaround（`runtime/workspace-registry.ts:271-279`）与 `runtime/cleanup-loop.ts:1` 的跨层 import。
- session-target 解析（`resolveSessionTarget` + 顶层 `workflowRunId`/`sessionName` legacy 回退）提取为独立模块。
- 连接存活/重连 helper（`probeLiveness`/`forceReconnect`/`notifyReconnected`）提取到 `liveness-probe.ts`。
- handler 注册按簇拆分为 `register*Handlers(connection, deps)`：git 查询（`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetWorkspaceStatus`/`GetFileContent`）、workspace 移除（`RemoveWorkspace`）、followup（`ReceiveFollowup`）、cancel（`CancelAgentSession`）、workflow-run-status（`ReceiveWorkflowRunStatus`）。
- **测试先行**：在迁移任何 handler 代码之前，先为 `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` 及其解析器补齐直接测试（目前零覆盖）。
- **不变项（契约边界）**：SignalR 方法名、各 handler 返回形状、cancel 回复形状（`{ state }` 镜像进 HTTP 响应）、hub URL 构造、重连策略（`[0,2000,5000,10000,30000]`）、followup 的 fire-and-forget 语义、legacy 顶层字段回退全部保持字节级不变。

## Capabilities

- `workspace-git-queries`: runner 响应 server 端 git workspace 查询（`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetWorkspaceStatus`/`GetFileContent`）的行为契约——workspace 路径解析（`resolveWorkspaceQuery` 拒绝缺失 `branch`/`baseBranch`、不再回退 `mo/issue-{N}`）、git worktree 探测、各 handler 的返回形状（diff 文件列表含 binary 标记、commits 数组、ahead/behind、mergeBase 回退、file content 的 base/head 双查、rebase 冲突文件列表）、解析器对 numstat/ahead-behind/log 的容错，以及缺失 workspace 时返回 `null`/`{ exists: false }` 的约定。
- `runner-connection-liveness`: SignalR 连接生命周期——hub URL 构造（`{baseUrl}/hubs/runner?runnerId=&buildGitHash=`）、重连策略序列、`probeLiveness` 的超时/abort/非 Connected 状态语义、`forceReconnect` 的 stop→start 顺序与 stop 错误吞咽、`onReconnected` 回调触发（含 connectionId 缺失时回落 `connection.connectionId`）。
- `runner-signalr-push-handlers`: server 推送/调用类 handler——`ReceiveFollowup` 的 fire-and-forget 语义（不 await `connection.prompt`、runtime event emit 失败不阻塞 prompt、resolver 抛错/返回 null 静默丢弃）、session-target 解析（`target.kind` 判别 + 顶层 `workflowRunId`/`sessionName` legacy 回退）、`CancelAgentSession` 的回复状态（`cancelled`/`not-cancellable` 及 cancel send 失败回落）、`ReceiveWorkflowRunStatus` 的 registry `active→eligible` 幂等迁移、`RemoveWorkspace` 的 runner-root 包含检查与 registry 一致性。

## Impact

- **Runner 实现**（`packages/runner/src/server/`）：`runner-signalr.ts` 从 850 行收敛为组装 + 委托；新增模块承接 git 解析器、workspace 路径解析、session-target 解析、liveness probe、各 handler 簇的 `register*Handlers`。`connection.ts` 不变。
- **Runner 跨层**（`packages/runner/src/runtime/`）：`cleanup-loop.ts:1` 改从新路径解析模块导入 `isUnderRunnerRoot`（消除跨层 import）；`workspace-registry.ts:271-279` 删除 `isPathUnder` 的循环 import workaround 注释与重复实现，改调权威实现。
- **Runner 测试**（`packages/runner/tests/`）：新增 `GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent` handler 与解析器的直接测试（测试先行）；`runner-signalr.spec.ts` / `runner-signalr-workflow-status.spec.ts` / `workspace-registry-integration.spec.ts` / `cleanup-loop.spec.ts` 的 import 路径随模块迁移更新，断言不变。
- **无 API / 线上契约 / 时序 / 配置变化**：所有 SignalR 方法名、返回形状、hub URL、重连间隔、followup/cancel 语义保持字节级不变；server 侧零改动；Web/CLI 零改动。
- **无迁移、无新依赖**。
