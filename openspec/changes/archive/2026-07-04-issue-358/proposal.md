## Why

`Mohist:SystemUpdate:Enabled` 是挡住系统更新启动的安全闸门，但更新子系统存在两处独立的"是否启用"判定，逻辑已漂移：`SystemInfoService.IsUpdateEnabled`（展示路径）用显式控制流，稳健正确；`SystemUpdateService.IsUpdateEnabled`（启动路径）写成单行 `isNullOrBlank || TryParse && enabled`，靠 `&&` 比 `||` 紧才"恰好"对 `Enabled="false"` 返回 false。这种正确是脆弱平衡——任何翻转默认、加子句、改默认值的重构都会让禁用开关静默失效，而编译器和现有测试（`SystemUpdateServiceSpecs` 只覆盖 `"true"`，从未覆盖 `"false"` 启动路径）都发现不了。两实现差异也让阅读者无法判断哪一个是真相，闸门失去可信度。现在修是因为风险低（单子系统、无迁移、无 API 契约变化）且缺陷就在安全关键路径上。

## What Changes

- 重写 `SystemUpdateService.IsUpdateEnabled`（`SystemUpdateService.cs:605-609`）为与 `SystemInfoService.IsUpdateEnabled` 同构的显式控制流：`if (!string.IsNullOrWhiteSpace(configured)) return bool.TryParse(configured, out var v) && v; return <既保留的默认>;`，消除对运算符优先级的依赖。
- 保留 `SystemUpdateService.IsUpdateEnabled` 既有的未配置默认行为（空/null → `true`，因为 install mode 与 install completeness 已由 `ValidateStart` 的前置/后置校验独立把关，不在本 gate 内重复）。
- 新增 spec：在 `SystemUpdateServiceSpecs` 中锁定 `Mohist:SystemUpdate:Enabled="false"` 时 `StartAsync` 返回 `(Started=false, Code="update_disabled")`，补齐当前缺失的启动路径覆盖。
- 在 spec 层断言两个 `IsUpdateEnabled` 实现在 `true`/`false`/未配置 三档相同输入下行为一致（共享表驱动断言或各自覆盖三档）。
- 不改 `SystemInfoService.IsUpdateEnabled` 实现（已正确）；不统一为单一函数；不动其它启动校验（install mode、dirty source、no update available）。

## Capabilities

- `system-update-start-gate`: `Mohist:SystemUpdate:Enabled` 开关如何闸住 `SystemUpdateService.StartAsync` 启动路径——显式 `true` 启用、显式 `false` 返回 `update_disabled`、未配置走保留默认；gate 判定结构不得依赖运算符优先级，且须与展示路径（`SystemInfoService.IsUpdateEnabled`）在相同输入下行为一致。

## Impact

- **Server 实现**：`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`——`IsUpdateEnabled()`（约 605-609 行）控制流重写，纯结构变更，外部可观察行为不变。
- **Server 测试**：`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs`——新增 `Enabled="false"` → `update_disabled` 启动路径 spec；新增/补齐三档（`true`/`false`/未配置）一致性断言。`SystemInfoServiceSpecs.cs` 已覆盖 `"false"` 与 `"true"`，无需改动，仅作为一致性对照基线。
- **无 API/契约/存储/依赖变化**：`StartAsync` 返回元组形状、`update_disabled` code 字符串、配置键 `Mohist:SystemUpdate:Enabled` 均保持不变。
- **无迁移、无 Runner/Web/CLI 改动**。
