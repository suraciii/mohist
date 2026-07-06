### Requirement: The verb-root `mo update` group is the single entry point for updating CLI, server, and runner from source

After this change, source-code updates of any component SHALL be reachable only through the verb-root update group: `mo update` (all components), `mo update cli`, `mo update server`, and `mo update runner`. These four paths SHALL continue to invoke the same `SourceCodeUpdater` methods they invoked before this change (`UpdateAllAsync`, `UpdateCliAsync`, `UpdateServerAsync`, `UpdateRunnerAsync`), with the same option set (`--repo-root`, `--dry-run`, `--cli-path` on the cli/all paths, plus the internal hidden `--continue-after-cli-update` flag on `mo update`), the same defaults, and the same exit-code / output semantics. No updater flag, default, stage-machine behavior, or observable side effect SHALL change as a side effect of converging the entry points.

#### Scenario: mo update (all components) behaves identically after the convergence

- **WHEN** a caller runs `mo update` (with or without `--repo-root`, `--cli-path`, `--dry-run`) after this change
- **THEN** the CLI SHALL resolve the bare `update` verb-root
- **AND** SHALL invoke `SourceCodeUpdater.UpdateAllAsync` with the same arguments and the same stage-machine ordering it produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

#### Scenario: mo update cli behaves identically after the convergence

- **WHEN** a caller runs `mo update cli` (with or without `--repo-root`, `--cli-path`, `--dry-run`) after this change
- **THEN** the CLI SHALL resolve `cli` as a subcommand of the `update` verb-root
- **AND** SHALL invoke `SourceCodeUpdater.UpdateCliAsync` with the same arguments it produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

#### Scenario: mo update server behaves identically after the convergence

- **WHEN** a caller runs `mo update server` (with or without `--repo-root`, `--dry-run`) after this change
- **THEN** the CLI SHALL resolve `server` as a subcommand of the `update` verb-root
- **AND** SHALL invoke `SourceCodeUpdater.UpdateServerAsync` with the same arguments it produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

#### Scenario: mo update runner behaves identically after the convergence

- **WHEN** a caller runs `mo update runner` (with or without `--repo-root`, `--dry-run`) after this change
- **THEN** the CLI SHALL resolve `runner` as a subcommand of the `update` verb-root
- **AND** SHALL invoke `SourceCodeUpdater.UpdateRunnerAsync` with the same arguments it produced before this change
- **AND** the process exit code and stdout/stderr SHALL match the pre-change verb-root behavior

### Requirement: The `update` subcommand is removed from the `server` resource group

The `server` resource group (`mo server`) SHALL no longer register an `update` subcommand. Invoking `mo server update` SHALL fail to resolve, exit non-zero with a parse error, and SHALL NOT call `SourceCodeUpdater.UpdateServerAsync` or any other updater method, and SHALL NOT produce any update side effect (no source rebuild, no service restart, no runner stop/restore). The `SourceCodeUpdater.UpdateServerAsync` method signature and its DI registration SHALL remain unchanged — only the redundant `mo server update` command path is removed.

#### Scenario: mo server update fails to resolve and triggers no updater call

- **WHEN** a caller runs `mo server update` (with any combination of update flags) after this change
- **THEN** the CLI SHALL fail to resolve `update` as a subcommand of `server`
- **AND** SHALL exit non-zero
- **AND** the `SourceCodeUpdater` SHALL NOT have its `UpdateServerAsync` (or any other method) invoked by this invocation

### Requirement: The runner resource group continues to expose no `update` subcommand

Runner source-code update has never been registered under the `runner` resource group — `mo runner update` has never resolved. This change SHALL preserve that invariant: the `runner` resource group SHALL NOT register an `update` subcommand, and invoking `mo runner update` SHALL fail to resolve and exit non-zero without invoking any `SourceCodeUpdater` method. Runner updates SHALL continue to be reachable solely through the verb-root `mo update runner`. This requirement is stated explicitly so the convergence is symmetric and a future change cannot silently reintroduce `mo runner update` as an alias.

#### Scenario: mo runner update does not resolve (pre-existing invariant preserved)

- **WHEN** a caller runs `mo runner update` (with any combination of flags) after this change
- **THEN** the CLI SHALL fail to resolve `update` as a subcommand of `runner`
- **AND** SHALL exit non-zero
- **AND** the `SourceCodeUpdater` SHALL NOT have any method invoked by this invocation
- **AND** runner source-code update SHALL remain reachable only via `mo update runner`

### Requirement: The `server` and `runner` resource groups advertise only their non-update subcommands after the convergence

Removing the `update` subcommand from the `server` group SHALL NOT remove, rename, or alter any other subcommand of the `server` or `runner` groups. `mo server --help` SHALL continue to advertise `start`, `stop`, `restart`, `status`, `logs`, `health`, `uninstall`, and `info`, and SHALL NOT advertise `update`. `mo runner --help` SHALL continue to advertise `start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`, `list`, `show`, and `status`. Each surviving subcommand SHALL continue to resolve and dispatch to the same handler it used before this change, with the same options and defaults.

#### Scenario: mo server --help advertises the surviving subcommands and drops update

- **WHEN** a caller runs `mo server --help` after this change
- **THEN** the advertised subcommand list SHALL include `start`, `stop`, `restart`, `status`, `logs`, `health`, `uninstall`, and `info`
- **AND** SHALL NOT include `update`

#### Scenario: mo runner --help is unchanged by the update convergence

- **WHEN** a caller runs `mo runner --help` after this change
- **THEN** the advertised subcommand list SHALL include `start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`, `list`, `show`, and `status`
- **AND** SHALL NOT include `update` (preserving the pre-existing invariant)

#### Scenario: surviving server and runner subcommands still resolve and execute

- **WHEN** a caller runs a surviving `mo server <subcommand>` or `mo runner <subcommand>` (e.g. `mo server restart`, `mo runner status`) after this change
- **THEN** the CLI SHALL resolve the subcommand and dispatch to the same handler, with the same options and defaults, as before this change

### Requirement: `docs/cli-reference.md` reflects the converged update entry points and drops the update-side gap rows

`docs/cli-reference.md` SHALL be updated to match the converged update command surface. The Server command-group code block SHALL NOT list `mo server update`. The命令路径迁移 (command-path migration) note that referenced the `mo server install/update` ↔ `mo install/update` convergence SHALL be removed, because the convergence this change delivers closes the gap the note tracked. The实装差距 (implementation-gap) table SHALL NOT retain the row that tracked the update double-entry convergence (`mo server install/update` → `mo install/update`). The「安装与升级（动词根集中）」section already documents the target update paths (`mo update`, `mo update cli`, `mo update server`, `mo update runner`) and SHALL remain the canonical statement of the update command surface.

#### Scenario: the server section no longer lists mo server update

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the Server command-group code block SHALL NOT contain a `mo server update` line
- **AND** SHALL continue to list the non-update server subcommands

#### Scenario: the command-path migration note is removed

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the Server section SHALL NOT carry the命令路径迁移 note that told callers the `mo server install/update` ↔ `mo install/update` double-entry was converging
- **BECAUSE** this change closes the gap that note tracked

#### Scenario: the update-side gap row is removed from the gap table

- **WHEN** the实装差距 table in `docs/cli-reference.md` is read after this change
- **THEN** the table SHALL NOT contain the row whose current-implementation column references `mo server install/update` as a double-entry pending convergence to `mo install/update`
