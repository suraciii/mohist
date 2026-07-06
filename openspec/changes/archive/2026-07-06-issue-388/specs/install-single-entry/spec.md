### Requirement: The verb-root `mo install` group is the single entry point for installing server and runner as managed services

After this change, installation of the server or runner as a managed background service SHALL be reachable only through the verb-root install group: `mo install server` and `mo install runner`. These two paths SHALL continue to invoke the same `IServiceInstaller.InstallServerAsync` and `IServiceInstaller.InstallRunnerAsync` methods they invoked before this change, with the same option set (`--repo-root`, `--listen-url` on server, `--server-url` and `--runner-root` on runner, plus the shared `--dry-run` and `--unit-dir`), the same defaults, and the same exit-code / output semantics. No installer flag, default, or observable side effect SHALL change as a side effect of converging the entry points.

#### Scenario: mo install server behaves identically after the convergence

- **WHEN** a caller runs `mo install server` (with or without `--repo-root`, `--listen-url`, `--dry-run`, `--unit-dir`) after this change
- **THEN** the CLI SHALL resolve the command under the `install` verb-root
- **AND** SHALL invoke `IServiceInstaller.InstallServerAsync` with a `ServiceInstallOptions` carrying the same field values the verb-root produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

#### Scenario: mo install runner behaves identically after the convergence

- **WHEN** a caller runs `mo install runner` (with or without `--repo-root`, `--server-url`, `--runner-root`, `--dry-run`, `--unit-dir`) after this change
- **THEN** the CLI SHALL resolve the command under the `install` verb-root
- **AND** SHALL invoke `IServiceInstaller.InstallRunnerAsync` with a `ServiceInstallOptions` carrying the same field values the verb-root produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

### Requirement: The `install` subcommand is removed from the `server` resource group

The `server` resource group (`mo server`) SHALL no longer register an `install` subcommand. Invoking `mo server install` SHALL fail to resolve, exit non-zero with a parse error, and SHALL NOT call `IServiceInstaller.InstallServerAsync` or any other installer method, and SHALL NOT produce any install side effect (no unit file writes, no service creation, no systemd interaction). The `IServiceInstaller.InstallServerAsync` method signature and its registration in the DI container SHALL remain unchanged — only the redundant `mo server install` command path is removed.

#### Scenario: mo server install fails to resolve and triggers no installer call

- **WHEN** a caller runs `mo server install` (with any combination of install flags) after this change
- **THEN** the CLI SHALL fail to resolve `install` as a subcommand of `server`
- **AND** SHALL exit non-zero
- **AND** the `IServiceInstaller` service SHALL NOT have its `InstallServerAsync` (or any other method) invoked by this invocation

### Requirement: The `install` subcommand is removed from the `runner` resource group

The `runner` resource group (`mo runner`) SHALL no longer register an `install` subcommand. Invoking `mo runner install` SHALL fail to resolve, exit non-zero with a parse error, and SHALL NOT call `IServiceInstaller.InstallRunnerAsync` or any other installer method, and SHALL NOT produce any install side effect. The `IServiceInstaller.InstallRunnerAsync` method signature and its DI registration SHALL remain unchanged — only the redundant `mo runner install` command path is removed.

#### Scenario: mo runner install fails to resolve and triggers no installer call

- **WHEN** a caller runs `mo runner install` (with any combination of install flags) after this change
- **THEN** the CLI SHALL fail to resolve `install` as a subcommand of `runner`
- **AND** SHALL exit non-zero
- **AND** the `IServiceInstaller` service SHALL NOT have its `InstallRunnerAsync` (or any other method) invoked by this invocation

### Requirement: The `server` and `runner` resource groups advertise only their non-install subcommands after the convergence

Removing the `install` subcommand from the `server` and `runner` groups SHALL NOT remove, rename, or alter any other subcommand of those groups. `mo server --help` SHALL continue to advertise `start`, `stop`, `restart`, `status`, `logs`, `health`, `uninstall`, and `info`, and SHALL NOT advertise `install`. `mo runner --help` SHALL continue to advertise `start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`, `list`, `show`, and `status`, and SHALL NOT advertise `install`. Each surviving subcommand SHALL continue to resolve and dispatch to the same handler it used before this change, with the same options and defaults.

#### Scenario: mo server --help advertises the surviving subcommands and drops install

- **WHEN** a caller runs `mo server --help` after this change
- **THEN** the advertised subcommand list SHALL include `start`, `stop`, `restart`, `status`, `logs`, `health`, `uninstall`, and `info`
- **AND** SHALL NOT include `install`

#### Scenario: mo runner --help advertises the surviving subcommands and drops install

- **WHEN** a caller runs `mo runner --help` after this change
- **THEN** the advertised subcommand list SHALL include `start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`, `list`, `show`, and `status`
- **AND** SHALL NOT include `install`

#### Scenario: surviving server and runner subcommands still resolve and execute

- **WHEN** a caller runs a surviving `mo server <subcommand>` or `mo runner <subcommand>` (e.g. `mo server status`, `mo runner list`) after this change
- **THEN** the CLI SHALL resolve the subcommand and dispatch to the same handler, with the same options and defaults, as before this change

### Requirement: `docs/cli-reference.md` reflects the converged install entry points and drops the install-side gap rows

`docs/cli-reference.md` SHALL be updated to match the converged install command surface. The Runner command-group code block SHALL NOT list `mo runner install`. The Server command-group code block SHALL NOT list `mo server install`. The实装差距 (implementation-gap) table SHALL NOT retain the row that tracked the install double-entry convergence (`mo server install/update` / `mo runner install/update` → `mo install/update`). The「安装与升级（动词根集中）」section already documents the target install paths (`mo install server`, `mo install runner`) and SHALL remain the canonical statement of the install command surface.

#### Scenario: the runner section no longer lists mo runner install

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the Runner command-group code block SHALL NOT contain a `mo runner install` line
- **AND** the section SHALL continue to point readers to the「安装与升级」section for installation

#### Scenario: the server section no longer lists mo server install

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the Server command-group code block SHALL NOT contain a `mo server install` line
- **AND** SHALL continue to list the non-install server subcommands (`start`, `stop`, `restart`, `health`, `info`, `status`, `logs`, `uninstall`)

#### Scenario: the install-side gap row is removed from the gap table

- **WHEN** the实装差距 table in `docs/cli-reference.md` is read after this change
- **THEN** the table SHALL NOT contain the row whose current-implementation column references `mo server install/update` or `mo runner install/update` as a double-entry pending convergence to `mo install/update`
