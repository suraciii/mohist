### Requirement: Explicit disable blocks the update start path

`SystemUpdateService.StartAsync` SHALL consult the `Mohist:SystemUpdate:Enabled` configuration before starting any update work. When the value explicitly parses to `false` (`"false"`), the gate MUST reject the request with `Code = "update_disabled"` and MUST NOT acquire the update lock, persist a job state, or run any update command. The rejection reason text SHALL be `"System update is disabled by configuration"`.

#### Scenario: Enabled="false" rejects StartAsync without side effects

- **WHEN** `StartAsync` is invoked with `Mohist:SystemUpdate:Enabled = "false"` and an otherwise startable install (local-source, complete install, update available, clean source)
- **THEN** it returns `(Started = false, Error = "System update is disabled by configuration", Code = "update_disabled", Status = null)`
- **AND** no update lock is acquired, no job state is persisted, and no update command runs

#### Scenario: Disabled gate is ordered before dirty-source and no-update-available checks

- **WHEN** `StartAsync` is invoked with `Enabled = "false"` on an install that is also dirty-source or has no update available
- **THEN** the response is still `Code = "update_disabled"` (the disable gate takes precedence over the later dirty-source / no-update-available checks within the start-path validation sequence)

### Requirement: Explicit enable permits the update start path

When `Mohist:SystemUpdate:Enabled` explicitly parses to `true` (`"true"`), the enablement gate SHALL pass and `StartAsync` SHALL proceed to the remaining start-path validations (install mode, install completeness, dirty source, update availability) and command execution. A passing gate MUST NOT be the sole determinant of whether an update actually starts.

#### Scenario: Enabled="true" passes the gate and proceeds to other validations

- **WHEN** `StartAsync` is invoked with `Enabled = "true"`
- **THEN** the enablement gate does not reject the request; the outcome is governed entirely by the remaining start-path validations (e.g. `update_in_progress`, `unsupported_install`, `install_incomplete`, `dirty_source`, `no_update_available`, or a successful start)

### Requirement: Unconfigured value preserves the default-enabled gate

When `Mohist:SystemUpdate:Enabled` is unset, null, empty, or whitespace-only, the enablement gate SHALL default to enabled and MUST NOT reject the request on enablement grounds. Install mode and install completeness SHALL continue to be enforced as independent start-path checks (the gate does not duplicate them), preserving the existing default behavior for unconfigured deployments.

#### Scenario: Unconfigured Enabled does not trigger update_disabled

- **WHEN** `StartAsync` is invoked with `Enabled` unset, null, empty, or whitespace-only on a local-source install
- **THEN** the response is never `Code = "update_disabled"`; the install-mode check and the install-completeness check independently determine whether the start proceeds

#### Scenario: Default-enabled gate behavior is unchanged by this change

- **WHEN** `StartAsync` is invoked with an unconfigured `Enabled` value under the same install conditions that started an update before this change
- **THEN** the request reaches the same start/no-start decision and executes the same commands as before this change

### Requirement: Gate implementation independent of operator precedence

The `SystemUpdateService.IsUpdateEnabled` gate decision SHALL be expressed as explicit control flow — first short-circuit on whether a configuration value is present, then parse and return the parsed boolean — and MUST NOT be expressed as a single-line boolean expression whose correctness depends on the relative precedence of `&&` versus `||`. The structure SHALL be isomorphic to `SystemInfoService.IsUpdateEnabled`'s explicit-value handling: `if (!string.IsNullOrWhiteSpace(configured)) return bool.TryParse(configured, out var value) && value; return <preserved default>;`.

#### Scenario: Source audit rejects precedence-dependent single-line gate

- **WHEN** the `SystemUpdateService.IsUpdateEnabled` implementation is inspected
- **THEN** it is written as an explicit control-flow form (a presence check followed by a `bool.TryParse(...) && value` return, then a separate default return), not as `string.IsNullOrWhiteSpace(configured) || bool.TryParse(configured, out var enabled) && enabled` or any other expression relying on operator precedence

### Requirement: Parity with the display-path enablement gate

For explicit boolean configuration values (`"true"` and `"false"`), the start-path enablement gate (`SystemUpdateService.IsUpdateEnabled`) SHALL produce the same enable/disable decision as the display-path enablement gate (`SystemInfoService.IsUpdateEnabled`), so that a disabled update is consistently reflected on both the "show update availability" path and the "start update" path.

#### Scenario: Explicit true agrees on both paths

- **WHEN** `Mohist:SystemUpdate:Enabled = "true"` is supplied to both enablement gates
- **THEN** both gates report the update as enabled

#### Scenario: Explicit false agrees on both paths

- **WHEN** `Mohist:SystemUpdate:Enabled = "false"` is supplied to both enablement gates
- **THEN** both gates report the update as disabled, yielding `update_disabled` on the start path and the disabled presentation on the display path
