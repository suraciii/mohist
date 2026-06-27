## Why

`mo update` 的 `VerifyRuntime` 阶段判定 runner 更新成功只看 systemd 服务状态是否为 `active`，不比对 runner 实际运行的二进制 `buildGitHash` 与 source HEAD。结果：runner 可能部署了携带旧 git hash 的二进制、运行过时代码，却仍以 `[ok] Runner connection: Runner service is active` 报告成功，用户直到 workflow 行为异常才发现 runner 没真正更新。Server 已经持有并在 `/api/runner/identity` 暴露 runner 上报的 `buildGitHash`，且已有对称的 server-identity 检查（`CheckServerIdentityAsync`）——runner 缺少的只是等价的 identity 校验。

## What Changes

- 在 `mo update` 的 `VerifyRuntime` 阶段新增 runner build-identity 校验，行为与现有 `CheckServerIdentityAsync` 对称：读取 runner 上报的 `buildGitHash`，与 source HEAD（`git rev-parse HEAD`）比对。
- 一致 → `Pass`（`[ok] Runner identity: Runner identity matches source HEAD '<hash>'`）。
- 不一致 → `Warn`（`[warn] Runner identity: Runner buildGitHash '<runner>' does not match source HEAD '<source>'`），不阻塞更新（runner 可能仍在重连）。
- runner 未上报 hash（null/empty）→ `Warn`，不阻塞更新。
- 保留现有 `CheckRunnerConnectionAsync` 的 `active` 状态检查不变——identity 校验是叠加，不是替换。

## Capabilities

### New Capabilities

- `update-runtime-consistency`: `mo update` 的 `VerifyRuntime` 阶段对各组件（server identity、runner connection、**runner identity**、web assets、CLI binary、managed skill assets）执行运行时一致性校验的行为契约，以及每项校验的 Pass/Warn/Fail 输出语义。当前这些行为只存在于实现中，无对应 spec；本变更首次将其形式化，并新增 runner build-identity 校验要求。

### Modified Capabilities

（无。runner `buildGitHash` 已由 runner 经 SignalR 握手与心跳上报、并由 `/api/runner/identity` 暴露；本次不改动 server API 契约或 runner 上报机制。）

## Impact

- **CLI**：`packages/cli/Mohist.Cli/Update/RuntimeConsistencyValidator.cs` 新增 `CheckRunnerIdentityAsync`（消费 `/api/runner/identity`，复用既有 `TryGetSourceHeadAsync`）；`SourceCodeUpdater` 的 VerifyRuntime 编排将其接入检查序列。
- **测试**：`packages/cli/tests/Mohist.Cli.Tests/` 新增/扩展 runtime 校验用例（匹配、不匹配、hash 缺失三态）。
- **Server / Runner**：无改动——`buildGitHash` 上报机制与 `/api/runner/identity` 端点均已具备。
- **构建/依赖**：无新增依赖。
