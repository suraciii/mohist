## Context

`Mohist:SystemUpdate:Enabled` 是挡住系统更新启动的安全闸门。更新子系统存在两处独立的"是否启用"判定，逻辑已漂移：

- **展示路径** — `SystemInfoService.IsUpdateEnabled`（`SystemInfoService.cs:141-153`）：显式控制流，先短路非空配置，再 `bool.TryParse(...) && explicitValue`，未配置时回落到 local-source 条件默认。逻辑稳健。
- **启动路径** — `SystemUpdateService.IsUpdateEnabled`（`SystemUpdateService.cs:605-609`）：单行 `string.IsNullOrWhiteSpace(configured) || bool.TryParse(configured, out var enabled) && enabled`，因 `&&` 比 `||` 紧而解析为 `isNullOrBlank || (parsed && enabled)`。对 `Enabled="false"` 恰好返回 `false`，但这种正确依赖运算符优先级的脆弱平衡——翻转默认、加第三个子句、改默认值都会让禁用开关静默失效，编译器和现有测试都发现不了。

测试侧同样有缺口：`SystemUpdateServiceSpecs` 的工厂（`CreateService` `SystemUpdateServiceSpecs.cs:1530-1533`、`CreateConsistencyService` `:1572-1575`）硬编码 `Enabled="true"`，从未覆盖 `"false"` 启动路径；`update_disabled` 这条安全关键分支零覆盖。`SystemInfoServiceSpecs` 已覆盖 `"false"`（`:99`）与 `"true"`（`:334`），作为一致性对照基线。

**关键约束（决定设计形态）**：两个 `IsUpdateEnabled` 的未配置默认行为**故意不同**，并非实现漂移——
- `SystemInfoService` 默认 `install.Mode == "local-source" && SourcePath 非空 && ServerUnit 非空`（展示路径没有独立的 install-mode 校验，需自带）。
- `SystemUpdateService` 默认 `true`，因为 `ValidateStart`（`SystemUpdateService.cs:585-603`）已在 gate 之前（`:587`）与之后（`:593-594`）独立校验 install mode 与 install completeness，gate 不重复。

因此本设计追求的是**控制流结构同构**（presence check → `TryParse && value` → 独立默认返回），而非两个函数体一致。约束：单子系统、无 API 契约变化、无存储/迁移、不动 `SystemInfoService` 实现、不统一为单一函数、risk=low。

## Goals / Non-Goals

**Goals:**
- 把 `SystemUpdateService.IsUpdateEnabled` 重写为与 `SystemInfoService` 同构的显式控制流，消除对 `&&`/`||` 优先级的依赖。
- 保留 `SystemUpdateService.IsUpdateEnabled` 既有未配置默认（空/null → `true`），不改变 `Enabled` 未配置时的可观察启动行为。
- 用 spec 锁死 `Enabled="false"` → `StartAsync` 返回 `(Started=false, Code="update_disabled")` 的启动路径，补齐当前零覆盖的安全分支。
- 用源码审计 spec 防止未来回归到优先级依赖的单行写法（复用本文件已有的 `SourceAudit_*` 模式）。
- 在 spec 层断言两个 gate 对显式 `"true"`/`"false"` 输入行为一致。

**Non-Goals:**
- 不统一两个 `IsUpdateEnabled` 为单一函数——它们的默认行为故意不同（见 Context）。
- 不改 `SystemInfoService.IsUpdateEnabled` 实现（已正确）。
- 不调整 `Enabled` 未配置时的默认启用规则，不碰 install mode / dirty source / no-update-available 等其它启动校验。
- 不改 `StartAsync` 返回元组形状、`update_disabled` code 字符串、配置键名。

## Decisions

### D1 — 重写 `SystemUpdateService.IsUpdateEnabled` 为显式控制流，保留默认 `true`

目标形态（与 `SystemInfoService.IsUpdateEnabled:144-152` 同构）：

```csharp
private bool IsUpdateEnabled()
{
    var configured = _configuration["Mohist:SystemUpdate:Enabled"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return bool.TryParse(configured, out var enabled) && enabled;
    }

    // Install mode and install completeness are enforced independently by ValidateStart
    // (before and after this gate), so the unconfigured default remains enabled.
    return true;
}
```

**Rationale**: 结构与展示路径对齐后，正确性不再依赖 `&&`/`||` 相对优先级；显式 `return true` 让"未配置默认启用"这一安全相关语义从隐式（`||` 短路）变为显式可读。保留默认 `true` 是因为 `ValidateStart`（`SystemUpdateService.cs:587` install mode、`:593-594` install completeness）已独立把关，gate 内重复会引入双重判定与潜在漂移。

**Alternatives considered**:
- *统一为单一共享函数*：拒绝。两个 gate 的默认行为故意不同（见 Context），强行统一要么让展示路径丢失 local-source 条件默认，要么让启动路径重复 install-mode 校验。属更大重构，明确排除出本 issue 范围。
- *翻转默认为 `false`（安全优先）*：拒绝。会改变现有未配置部署的可观察行为，违反"不改变默认行为"约束，且 install mode / completeness 已在 `ValidateStart` 把关，默认 `false` 无额外安全收益。

### D2 — 校验顺序保持不变（install-mode → enable-gate → completeness → dirty-source → availability）

`ValidateStart`（`SystemUpdateService.cs:585-603`）当前顺序：install mode（`:587`）→ enable gate（`:590`）→ install completeness（`:593`）→ dirty source（`:596`）→ availability（`:599`）。**不改顺序**。

**Rationale**: spec 的 "Disabled gate is ordered before dirty-source and no-update-available checks" scenario 已由现状满足（gate 在 `:590`，dirty/availability 在 `:596`/`:599`）。install-mode 排在 gate 之前是合理的——非 local-source 部署连更新都谈不上，无需先判断开关。重排会引入无依据的行为变化。

### D3 — 新增 `Enabled="false"` 启动路径 spec，扩展工厂以接受配置

现有 `CreateService` / `CreateConsistencyService` 硬编码 `Enabled="true"`。新增一个接受 `Enabled` 值（或完整配置覆盖）的重载/变体，供新增的 `StartAsync_DisabledByConfig_ReturnsUpdateDisabledWithoutSideEffects` 使用。该 spec 断言：
- 返回 `(Started=false, Error="System update is disabled by configuration", Code="update_disabled", Status=null)`；
- 无 store 写入、无 command 执行（沿用 `SystemUpdateServiceSpecs.cs:147-175` 已有的 mock 断言套路）。

**Rationale**: 复用已有工厂骨架最小化改动；显式配置注入让"哪条路径用了哪个 Enabled 值"在测试中可读。`Enabled="true"` 的对偶行为已被现有 `"true"` 工厂间接覆盖（gate 放行后由后续校验决定结果），spec 的 "Enabled='true' passes the gate" scenario 由现状满足，无需新增测试。

**Alternatives considered**:
- *改既有工厂签名加可选参数*：可行但会污染所有现有调用点；优先新增窄重载，保持现有 spec 不变。
- *只加运行时测试、不加源码审计*：拒绝。spec 显式要求 "Source audit rejects precedence-dependent single-line gate" scenario。运行时测试无法阻止未来重构回单行优先级写法——只要对 `"false"` 仍算出 `false`，单行写法就能通过所有运行时测试。

### D4 — 新增源码审计 spec，复用既有 `SourceAudit_*` 工具

复用本文件已建立的 `ReadSource()` / `FindMethodEnd()` 机制（先例：`SourceAudit_SaveAsyncOnlyInSharedHelpersAndStartAsync` `:1402`、`SourceAudit_AppendLogInvocationsStayOnSharedHelperPath` `:1423`）。新增 `SourceAudit_IsUpdateEnabledUsesExplicitControlFlow`：定位 `IsUpdateEnabled` 方法体，断言其中**不**出现 `string.IsNullOrWhiteSpace(...) ||` 与 `bool.TryParse(...) &&` 出现在同一 return 语句的单行组合，且**存在** `if (!string.IsNullOrWhiteSpace(` 与独立 `return true;`/`return ... enabled`。

**Rationale**: 源码审计是唯一能把"结构不依赖优先级"这一不变量固化下来的手段，与本 issue 的核心动机直接对齐。复用既有 helper 保持审计风格一致。

### D5 — 显式输入 parity 断言，表驱动且仅覆盖显式布尔值

新增表驱动断言，对 `"true"` / `"false"` 两个显式输入，断言两个 `IsUpdateEnabled` 产出相同 enable/disable 决策。**不**纳入未配置档位——因为两个 gate 的未配置默认故意不同（见 Context），强制 parity 会等于 D1 已拒绝的"统一默认"。

由于 `SystemInfoService.IsUpdateEnabled` 是 `private`，parity 断言通过对**两端各自的可观察结果**做对照实现，而非直接互调：
- 展示路径：`SystemInfoService.GetSystemInfo` 在 `Enabled="false"` 时返回 disabled 呈现（已有 spec 基线 `SystemInfoServiceSpecs.cs:99-110`）。
- 启动路径：新增 `Enabled="false"` → `update_disabled` spec（D3）。

二者并列即构成 spec 层 parity 证据。

**Alternatives considered**:
- *把 `IsUpdateEnabled` 改 `internal` 并直接互调做表驱动 parity*：拒绝。仅为测试改可见性是测试异味；且两个函数签名不同（一个带 `InstallDetectionResult`，一个无参），直接互调需额外适配，收益不抵成本。
- *parity 覆盖未配置档位*：拒绝，理由见上（默认故意不同）。

## Risks / Trade-offs

- **[风险] 源码审计 spec 可能对无害的重构产生误报**（例如有人把 `if (!IsNullOrWhiteSpace(...))` 换成 `if (!string.IsNullOrEmpty(...))`） -> 审计断言锁定**结构特征**（presence-check + 独立默认 return，禁止单行 `||` + `&&` 组合），而非具体 token；结构等价的重写不会触发误报。
- **[风险] 未来有人新增第三个 gate 子句再次引入优先级陷阱** -> 源码审计 spec 只守护当前方法体；通过 code review + 此 spec 的存在本身（命名清晰表达意图）兜底。完整守护超出本 issue 范围。
- **[权衡] 不统一为单一函数 = 默认差异永远存在，阅读者仍需理解两套默认** -> 用 `IsUpdateEnabled` 内的注释（见 D1 代码块）显式说明"为何默认 true"，把理解成本压到注释里，而非隐藏在 `||` 短路中。这是本 issue 可接受的最大修复边界。
- **[风险] 新增工厂重载若实现不当，可能让现有 `"true"` spec 静默走 disabled 路径** -> 新工厂必须默认复用 `"true"` 语义（或现有工厂不变、仅新增窄重载），并由现有 `"true"` 路径 spec 作为回归基线保护。

## Migration Plan

无需迁移。纯实现层结构重写，外部可观察行为不变：
- `StartAsync` 返回元组形状、`update_disabled` code、`Mohist:SystemUpdate:Enabled` 配置键均保持不变。
- `Enabled` 未配置（空/null/whitespace）时的启动决策与命令执行与改前完全一致（D1 保留默认 `true`）。

**部署**：随下一次 server 构建发布即可，无配置迁移、无存储 schema 变化、无 Runner/Web/CLI 协同改动。

**回滚**：单 commit 回退 `SystemUpdateService.cs:605-609` 即可恢复原实现；新增 spec 在回滚后会失败（这正是它们存在的意义——防止静默回退）。若需临时禁用守护性 spec，单独 revert 测试 commit，不影响生产行为。

## Open Questions

无。验收标准、默认保留、parity 范围、审计手段在 proposal 与 specs 中均已明确，D1–D5 各项均有确定性答案。
