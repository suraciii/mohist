## Why

`mo update` currently stops the runner before restarting the server and then waits silently during readiness checks — if the user interrupts or the process fails, the runner stays stopped, breaking the workflow system Mohist exists to run. The Web UI's system update status can also drift from runtime reality, leaving stale "waiting-for-reconnect" entries that contradict the actual running state.

## What Changes

- `mo update` displays product-level stages (Updating CLI, Preparing runner, Updating Server, Waiting for API readiness, Restoring runner, Verifying runtime) instead of implementation logs
- The runner-stopped window is made visible during the update, with bounded progress and current wait reason for long stages
- A recovery contract is implemented: if the runner was running before update, Mohist attempts to restore it on failure, timeout, or Ctrl-C interruption
- Ctrl-C enters the same recovery path, then prints whether recovery succeeded or what still needs action
- Post-update runtime consistency is verified: CLI installed and callable, Server responds with expected identity, Web assets are usable, Runner is connected, managed skill assets match the installed CLI
- Final CLI result states one of: ready, recovered with warnings, or failed with a specific unavailable capability
- **BREAKING**: Web system update status endpoint reconciles stale states — if a `waiting-for-reconnect` job belongs to an older runtime, it is marked as superseded rather than presented as current truth
- CLI-triggered and Web-triggered update paths share product semantics for stages, outcome, and recovery where practical

## Capabilities

### New Capabilities
- `update-recovery`: Recovery contract during `mo update` — runner restoration on failure, timeout, or user interruption; actionable final messages naming unavailable capabilities; Ctrl-C safe handling
- `runtime-consistency`: Post-update verification that CLI binary, Server API identity, Web assets, Runner connection, and managed skill assets are coherent and usable; single outcome of ready / recovered-with-warnings / failed-with-specific-capability

### Modified Capabilities
- `cli-interface`: Update command output changes from raw progress lines to product-level stages, recovery messages, and runtime consistency reporting
- `http-api`: System update status endpoint detects and reconciles stale `waiting-for-reconnect` state for older runtimes; runtime consistency verification surface
- `web-ui`: Settings/system runtime view shows latest update outcome without presenting stale states as current; CLI and Web update paths share consistent stage/outcome semantics
- `server-daemon`: Runner restore behavior during recovery after server update failure; readiness verification extended to cover identity and runtime consistency signals

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Update.cs`, `SourceCodeUpdater`): Major refactor to add stages, recovery, interruption handling, and runtime consistency verification
- **Server** (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`): Staleness reconciliation logic in `GetLatestStatusAsync`; runtime consistency API surface
- **Web** (`packages/web/src/entities/settings/`): Update status query and UI to handle superseded states; runtime health view with outcome display
- **Managed assets** (`SkillAssetSynchronizer`): Verification step that managed skill data matches the installed CLI expectation
- **Tests**: New specs for success, readiness timeout, user cancellation, runner restore success/failure, stale status reconciliation, and managed asset verification
