# Review Report

## Verdict: PASS (with warnings)

## Dimensions

### Correctness: PASS

- `tsc --noEmit` passes with zero errors on the backend source (server-systemd.ts, server.ts, server/index.ts, cli/index.ts).
- `generateServiceFile()` produces a valid systemd unit file with all required sections and fields.
- `runSystemctlUser()` correctly catches D-Bus error patterns and retries with `--machine <user>@ --user` for headless SSH.
- `detectInstallMode()` correctly resolves `__dirname` → `commands/` → `cli/` (3 levels up = `packages/cli/`), then checks for `bin/mo-server` to detect source mode.
- `getSystemdStatus()` correctly parses `systemctl show` output and handles unloaded/not-found services.
- `restartServer()` and `updateServer()` delegation logic is sound.
- SIGTERM handler in `server/index.ts:215` now calls `process.exit(143)` after graceful shutdown.
- `--print-logs` is read from `process.argv` in server `index.ts:59`, enabling stderr + file log output for systemd/journald capture.

**Warning:** `detectInstallMode()` lines 44-49 fall through to `globalScriptPath` which is the same path as `binMoServer` (same `path.join(cliPkgDir, 'bin', 'mo-server')`). This means the npm global detection path never produces a different `scriptPath`. The logic works by accident for source mode (detected first), but the npm global branch returns an identical path. In a real npm global install, `cliPkgDir` would resolve to the global `node_modules/mohist/` directory and `bin/mo-server` would exist there too, so it still works — but the code is misleading. The `globalScriptPath` variable is redundant since it duplicates the already-checked path.

**Warning:** `quoteIfNecessary()` (line 58-63) does not escape `$` or backtick characters. While systemd service files don't expand shell variables in ExecStart, the quoting strategy is minimal. Paths containing only spaces and quotes are handled; tab and other special characters are not considered.

### Complexity: PASS

- `server-systemd.ts` is 295 lines with 8 exported functions, all under 50 lines each.
- Cyclomatic complexity is low — the highest is `runSystemctlUser()` with a try/catch/retry pattern (~4 branches).
- No copy-pasted code; functions are focused and single-responsibility.
- Clean separation: systemd logic in `server-systemd.ts`, CLI orchestration in `server.ts`, commander registration in `cli/index.ts`.

### Test Coverage: PASS (with warnings)

- 24 tests in `tests/server-systemd.test.ts`, all passing.
- `generateServiceFile()`: 10 tests covering all sections, WorkingDirectory conditional, CR/LF rejection for all inputs, path quoting.
- `getSystemdStatus()`: 5 tests covering loaded/not-loaded/empty/error/missing-field states.
- `runLinger()`: 3 tests covering success, already-enabled, and error cases.
- `isSystemdServiceInstalled()`: 1 test (basic type check).
- `detectInstallMode()`: 2 tests (nodePath and scriptPath truthiness).

**Warning:** No tests for `runSystemctlUser()`, `installSystemdService()`, `uninstallSystemdService()`, `restartServer()`, or `updateServer()`. These are integration-level functions that call `execSync` and interact with the filesystem, but they could still be unit-tested with mocking (as done for `getSystemdStatus` and `runLinger`).

**Warning:** No tests for the modified `server.ts` delegation paths (start/stop/status when systemd is installed). The systemd delegation early-return blocks in `startServer()`, `stopServer()`, and `serverStatus()` are untested.

### Security: PASS

- CR/LF injection prevention in `validateNoCrlf()` correctly checks for both `\n` and `\r` in all values written to the service file.
- `runSystemctlUser()` does not construct commands from untrusted user input — `args` is always a hardcoded string from the callers.
- `quoteIfNecessary()` escapes double quotes in paths.
- No SQL injection, command injection, or credential exposure risks.
- `execSync` calls use `stdio: ['pipe', 'pipe', 'pipe']` to avoid leaking output to the parent process stdout.

**Note:** `runSystemctlUser()` constructs shell commands via template strings (e.g., `` `systemctl --user ${args}` ``). Since `args` is always caller-controlled (hardcoded strings like `'daemon-reload'` or `'start mohist.service'`), this is safe. However, the function signature accepts `string` which could theoretically be misused by future callers.

### Spec Compliance: PASS

#### T-001: Create server-systemd.ts with install mode detection and service file generation

| Criterion | Status | Notes |
|---|---|---|
| `detectInstallMode()` returns correct paths for source mode | PASS | Walks up 3 levels from `__dirname` to find `packages/cli/bin/mo-server`, then resolves repo root 2 more levels up |
| `detectInstallMode()` returns correct paths for npm global mode | PASS (with caveat) | Falls through when `bin/mo-server` not found. Returns same path variable — works but redundant (see Correctness warning) |
| `generateServiceFile()` produces valid content with [Unit], [Service], [Install] | PASS | Verified by tests and code inspection |
| `generateServiceFile()` includes WorkingDirectory only in source mode | PASS | Conditional on `workingDir` parameter |
| `generateServiceFile()` rejects `\n` or `\r` | PASS | `validateNoCrlf()` called for nodePath, scriptPath, and workingDir |
| `isSystemdServiceInstalled()` returns true/false | PASS | Simple `fs.existsSync` check |
| `process.execPath` used for node binary | PASS | Line 30: `const nodePath = process.execPath` |
| Typecheck passes | PASS | `tsc --noEmit` clean |

#### T-002: Implement systemctl helper with headless SSH fallback and linger management

| Criterion | Status | Notes |
|---|---|---|
| `runSystemctlUser('start mohist.service')` executes correct command | PASS | Template: `systemctl --user ${args}` |
| D-Bus error retry with `--machine <username>@ --user` | PASS | Catches 5 D-Bus error patterns, retries with `--machine` |
| `runLinger()` tolerates already-enabled | PASS | Checks stderr for "already enabled" and "Already enabled" |
| `getSystemdStatus()` returns `{ activeState, mainPID }` | PASS | Parsed from `systemctl show` output via regex |
| `getSystemdStatus()` returns null when not loaded | PASS | Checks for `not-loaded` and empty `Loaded=` line |
| Typecheck passes | PASS | |

#### T-003: Implement install and uninstall systemd service commands

| Criterion | Status | Notes |
|---|---|---|
| `installSystemdService()` writes to `~/.config/systemd/user/mohist.service` | PASS | Creates directory if needed, writes file |
| Runs daemon-reload, enable, start, linger | PASS | All called in sequence |
| Reinstall overwrites and restarts | PASS | `isReinstall` flag triggers `restart` instead of `enable+start` |
| Displays success message with service name | PASS | Shows service name, status command, logs command |
| `uninstallSystemdService()` runs `disable --now` | PASS | Falls back to separate `stop` + `disable` on failure |
| Deletes file and runs daemon-reload | PASS | `fs.unlinkSync` + `daemon-reload` |
| Shows "Service not installed" when absent | PASS | Returns early with yellow message |
| Typecheck passes | PASS | |

#### T-004: Add systemctl delegation to start/stop/status in server.ts

| Criterion | Status | Notes |
|---|---|---|
| `startServer()` delegates to systemctl when systemd installed | PASS | Early return path at line 71 |
| `startServer()` falls through when no systemd | PASS | After the `if (isSystemdServiceInstalled())` block |
| `stopServer()` delegates to systemctl | PASS | Early return at line 156 |
| `stopServer()` falls through | PASS | |
| `serverStatus()` shows systemd state with PID | PASS | Uses `getSystemdStatus()`, displays active/inactive/failed with color |
| `serverStatus()` falls through | PASS | |
| Existing spawn behavior preserved | PASS | No changes to existing code paths |
| Typecheck passes | PASS | |

#### T-005: Add SIGTERM exit code 143

| Criterion | Status | Notes |
|---|---|---|
| SIGTERM handler calls `process.exit(143)` | PASS | `server/index.ts:218` |
| SIGINT handler unchanged | PASS | Still calls `agentRunner.shutdown()` + `server.stop()` without forced exit code |
| Typecheck passes | PASS | |

#### T-006: Implement restart and update commands

| Criterion | Status | Notes |
|---|---|---|
| `restartServer()` delegates to systemctl when installed | PASS | |
| `restartServer()` calls stop+start when no systemd | PASS | Uses `fallbackStop`/`fallbackStart` parameters |
| `updateServer()` rejects npm global mode | PASS | Checks `!mode.workingDir`, shows message, exits 1 |
| `updateServer()` rejects when no systemd service | PASS | Checks `isSystemdServiceInstalled()` |
| `updateServer()` runs builds in `packages/cli/` and `packages/cli/web/` | PASS | |
| `updateServer()` aborts without restart on build failure | PASS | `process.exit(1)` in catch blocks |
| `updateServer()` runs systemctl restart after builds | PASS | |
| Typecheck passes | PASS | |

#### T-007: Register new CLI subcommands in commander

| Criterion | Status | Notes |
|---|---|---|
| `mo server install` registered | PASS | `cli/index.ts:53-58` |
| `mo server uninstall` registered | PASS | `cli/index.ts:60-65` |
| `mo server restart` registered | PASS | `cli/index.ts:67-72` |
| `mo server update` registered | PASS | `cli/index.ts:74-79` |
| `mo server --help` includes new subcommands | PASS | All registered as `.command()` on `serverCmd` |
| Existing commands still work | PASS | No changes to existing command registrations |
| Typecheck passes | PASS | |
| `npm run build` succeeds | N/A | Web build fails due to pre-existing React type errors in `web/` directory unrelated to this issue. Backend `tsc` compiles clean. |

## Fix Suggestions

1. **[server-systemd.ts:44-49]** The npm global fallback branch assigns `globalScriptPath` to the same value as `binMoServer` (`path.join(cliPkgDir, 'bin', 'mo-server')`). Remove the redundant `globalScriptPath` variable and just return `{ nodePath, scriptPath: path.join(cliPkgDir, 'bin', 'mo-server') }` directly, or add a comment explaining the intent.

2. **[server-systemd.ts:131]** `systemctl --machine ${username}@ --user ${args}` — the `username` is not shell-escaped. If a username contained shell metacharacters (rare but possible), this could be an issue. Consider quoting: `` `systemctl --machine '${username}'@ --user ${args}` ``.

3. **[server-systemd.ts:136]** Headless SSH detection only logs when `process.env.SSH_CONNECTION` is set, but the spec says "CLI displays a note about headless environment detection." Consider always displaying the note (not just when SSH_CONNECTION is set), since the D-Bus error already indicates a headless-like environment.

4. **[tests/server-systemd.test.ts]** Add tests for `runSystemctlUser()` headless SSH retry logic — this is the most complex function and has zero test coverage. Mock `execSync` to fail with a D-Bus error on the first call and succeed on the second.

5. **[tests/server-systemd.test.ts]** Add tests for the `installSystemdService()` and `uninstallSystemdService()` functions to verify the correct sequence of systemctl calls and file operations.

6. **[server-systemd.ts:80]** When quoting paths, if `nodePath` contains spaces but `scriptPath` doesn't (or vice versa), the `--print-logs` argument is placed outside the quotes correctly. However, if `nodePath` is quoted, systemd sees `"path/to/node" path/to/script --print-logs` which is correct. Verify with an integration test if possible.
