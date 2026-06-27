### Requirement: VerifyRuntime stage runs a fixed sequence of runtime consistency checks

`mo update` SHALL perform a `VerifyRuntime` stage after the build and restart stages that executes exactly these component checks, in this order: CLI binary, server build-identity, web assets, runner connection, runner build-identity, managed skill assets. Each check SHALL resolve to exactly one of three outcomes — `Pass`, `Warn`, or `Fail` — and SHALL be reported on its own line as `[ok] <component>: <message>`, `[warn] <component>: <message>`, or `[fail] <component>: <message>` respectively. The runner build-identity check SHALL be an addition layered on top of the runner connection check; the existing `active`-state check SHALL be preserved unchanged. When invoked with `--dry-run`, the stage SHALL execute no checks and SHALL print a single line listing the checks that would have run (including runner identity).

#### Scenario: Checks are reported one per line with their outcome prefix

- **WHEN** the user runs `mo update` and reaches the VerifyRuntime stage
- **THEN** the CLI SHALL print one line per check using the prefixes `[ok]`, `[warn]`, or `[fail]`
- **AND** each line SHALL identify its component (CLI binary, Server identity, Web assets, Runner connection, Runner identity, Managed skill assets)

#### Scenario: Dry run lists the checks without executing them

- **WHEN** the user runs `mo update --dry-run`
- **THEN** the VerifyRuntime stage SHALL NOT invoke any check
- **AND** SHALL print a dry-run line that mentions runner identity alongside the other checks

### Requirement: VerifyRuntime aggregates check outcomes into Ready, Recovered, or Failed

The VerifyRuntime stage SHALL derive a single stage outcome from the set of check outcomes. If any check resolves to `Fail`, the stage SHALL resolve to `Failed`, SHALL set the unavailable capability to the failing component, SHALL exit with a non-zero status, and SHALL NOT proceed. Otherwise, if any check resolves to `Warn` (and none to `Fail`), the stage SHALL resolve to `Recovered`, SHALL exit with status 0, and SHALL allow the update to be considered complete. Otherwise (all checks `Pass`) the stage SHALL resolve to `Ready`. A `Warn` SHALL never block the update; only a `Fail` SHALL block it.

#### Scenario: Any failure blocks the update

- **WHEN** one or more VerifyRuntime checks resolve to `Fail`
- **THEN** the stage SHALL resolve to `Failed`
- **AND** the CLI SHALL exit with a non-zero status
- **AND** the CLI SHALL record the first failing component as the unavailable capability

#### Scenario: Only warnings let the update succeed

- **WHEN** at least one check resolves to `Warn` and none resolves to `Fail`
- **THEN** the stage SHALL resolve to `Recovered`
- **AND** the CLI SHALL exit with status 0

#### Scenario: All passes resolve to Ready

- **WHEN** every check resolves to `Pass`
- **THEN** the stage SHALL resolve to `Ready`

### Requirement: CLI binary check verifies mo --version is invocable

The CLI binary check SHALL invoke the resolved `mo` binary with `--version`. When the CLI binary path was not resolved, the check SHALL resolve to `Fail` with a message instructing the user to reinstall with `mo update` or pass `--cli-path`. When `mo --version` exits with a non-zero code, the check SHALL resolve to `Fail` surfacing the stderr. When `mo --version` reports an empty version string, the check SHALL resolve to `Warn`. Otherwise the check SHALL resolve to `Pass` echoing the reported version.

#### Scenario: Version reported passes

- **WHEN** the resolved CLI binary responds to `--version` with a non-empty version string and exit code 0
- **THEN** the check SHALL resolve to `Pass`
- **AND** the reported message SHALL include the version string

#### Scenario: Unresolvable CLI binary path fails

- **WHEN** the CLI binary path was not resolved
- **THEN** the check SHALL resolve to `Fail`
- **AND** the message SHALL instruct the user to reinstall with `mo update` or pass `--cli-path`

#### Scenario: Non-zero exit fails

- **WHEN** `mo --version` exits with a non-zero code
- **THEN** the check SHALL resolve to `Fail`
- **AND** the message SHALL surface the stderr output

#### Scenario: Empty version warns

- **WHEN** `mo --version` exits 0 but reports an empty version string
- **THEN** the check SHALL resolve to `Warn`

### Requirement: Server build-identity check compares the running server git hash to source HEAD

The server build-identity check SHALL read the running server's git hash from `GET /api/system/info` (`data.running.gitHash`) and compare it to the source HEAD obtained from `git rev-parse HEAD` (resolved from the repository root, with a pre-populated context value taking precedence). When `/api/system/info` does not respond, the check SHALL resolve to `Fail`. When the server reports an empty git hash, the check SHALL resolve to `Warn` stating the hash is empty. When source HEAD cannot be determined, the check SHALL resolve to `Warn` stating the identity check is being skipped. When the two hashes differ, the check SHALL resolve to `Warn` quoting both the running hash and the source HEAD. When the two hashes match, the check SHALL resolve to `Pass`.

#### Scenario: Matching server hash passes

- **WHEN** the running server's git hash equals source HEAD
- **THEN** the check SHALL resolve to `Pass`
- **AND** the message SHALL identify the matched hash

#### Scenario: Server hash missing warns

- **WHEN** the server reports an empty git hash
- **THEN** the check SHALL resolve to `Warn`

#### Scenario: Source HEAD unavailable warns and skips

- **WHEN** source HEAD cannot be determined from `git rev-parse HEAD`
- **THEN** the check SHALL resolve to `Warn`
- **AND** the message SHALL state the identity check is being skipped

#### Scenario: Differing server hash warns without blocking

- **WHEN** the running server's git hash differs from source HEAD
- **THEN** the check SHALL resolve to `Warn` (not `Fail`)
- **AND** the message SHALL quote both the running hash and the source HEAD

#### Scenario: System info unreachable fails

- **WHEN** `GET /api/system/info` does not respond
- **THEN** the check SHALL resolve to `Fail`

### Requirement: Web assets check verifies the web root serves a bundle

The web assets check SHALL request the web root (`GET /`) and confirm it responds with `text/html` referencing an `/assets/*` bundle, then SHALL request the referenced asset path and confirm it responds with HTTP 200. When the web root returns a non-success status or a non-HTML content type, the check SHALL resolve to `Fail`. When the served HTML references no `/assets/*` bundle, the check SHALL resolve to `Fail`. When the referenced asset does not respond with HTTP 200, the check SHALL resolve to `Fail`. When any HTTP error is thrown, the check SHALL resolve to `Fail`. Otherwise the check SHALL resolve to `Pass`.

#### Scenario: Root HTML with a reachable asset bundle passes

- **WHEN** `GET /` returns HTML referencing `/assets/index-abc.js`
- **AND** `GET /assets/index-abc.js` returns HTTP 200
- **THEN** the check SHALL resolve to `Pass`

#### Scenario: Non-HTML root fails

- **WHEN** `GET /` returns a content type other than `text/html`
- **THEN** the check SHALL resolve to `Fail`

#### Scenario: Missing asset reference fails

- **WHEN** `GET /` returns HTML that references no `/assets/*` bundle
- **THEN** the check SHALL resolve to `Fail`

#### Scenario: Asset bundle unreachable fails

- **WHEN** the referenced `/assets/*` path does not return HTTP 200
- **THEN** the check SHALL resolve to `Fail`

### Requirement: Runner connection check verifies the runner service is active

The runner connection check SHALL read the runner service state from `GET /api/system/info` (`data.services.runner`) and SHALL resolve to `Pass` if and only if that state equals `active` (case-insensitive). When `/api/system/info` does not respond, the check SHALL resolve to `Fail`. When the server does not report a runner service state, the check SHALL resolve to `Fail`. When the reported state is anything other than `active`, the check SHALL resolve to `Fail` quoting the reported state. This `active`-state check SHALL remain unchanged by the addition of the runner build-identity check.

#### Scenario: Active runner passes

- **WHEN** `/api/system/info` reports `services.runner` equal to `active`
- **THEN** the check SHALL resolve to `Pass`

#### Scenario: Non-active runner fails

- **WHEN** `/api/system/info` reports a runner service state other than `active`
- **THEN** the check SHALL resolve to `Fail`
- **AND** the message SHALL quote the reported state

#### Scenario: Missing runner state fails

- **WHEN** `/api/system/info` reports no runner service state
- **THEN** the check SHALL resolve to `Fail`

#### Scenario: System info unreachable fails

- **WHEN** `GET /api/system/info` does not respond
- **THEN** the check SHALL resolve to `Fail`

### Requirement: Runner build-identity check compares the runner buildGitHash to source HEAD

The runner build-identity check SHALL be performed as an additional check after the runner connection check, behaviorally symmetric to the server build-identity check. It SHALL read the runner's reported `buildGitHash` from the runner identity endpoint and compare it to the source HEAD obtained from `git rev-parse HEAD` (resolved from the repository root, with a pre-populated context value taking precedence). The check SHALL never resolve to `Fail` because the runner may still be reconnecting. When the runner reports a non-empty `buildGitHash` that equals source HEAD, the check SHALL resolve to `Pass` with the message `Runner identity matches source HEAD '<source>'`. When the runner reports a non-empty `buildGitHash` that differs from source HEAD, the check SHALL resolve to `Warn` with the message `Runner buildGitHash '<runner>' does not match source HEAD '<source>'`. When the runner has not reported a `buildGitHash` (null or empty), the check SHALL resolve to `Warn`. When source HEAD cannot be determined, the check SHALL resolve to `Warn`.

#### Scenario: Matching runner hash passes

- **WHEN** the runner reports a non-empty `buildGitHash` equal to source HEAD
- **THEN** the check SHALL resolve to `Pass`
- **AND** the reported component SHALL be `Runner identity`
- **AND** the message SHALL be `Runner identity matches source HEAD '<source>'`

#### Scenario: Differing runner hash warns without blocking

- **WHEN** the runner reports a non-empty `buildGitHash` that differs from source HEAD
- **THEN** the check SHALL resolve to `Warn` (not `Fail`)
- **AND** the reported component SHALL be `Runner identity`
- **AND** the message SHALL be `Runner buildGitHash '<runner>' does not match source HEAD '<source>'`

#### Scenario: Missing runner buildGitHash warns

- **WHEN** the runner has not reported a `buildGitHash` (null or empty)
- **THEN** the check SHALL resolve to `Warn` (not `Fail`)
- **AND** the update SHALL NOT be blocked

#### Scenario: Source HEAD unavailable warns

- **WHEN** source HEAD cannot be determined from `git rev-parse HEAD`
- **THEN** the check SHALL resolve to `Warn`

#### Scenario: Runner identity endpoint unreachable warns

- **WHEN** the runner identity endpoint does not respond
- **THEN** the check SHALL resolve to `Warn` (not `Fail`)
- **AND** the update SHALL NOT be blocked

#### Scenario: Identity check layers on top of the active-state check

- **WHEN** the runner connection check has resolved to `Pass` (active)
- **THEN** the runner build-identity check SHALL still be performed
- **AND** the active-state check's `Pass` outcome SHALL remain unchanged

### Requirement: Managed skill assets check verifies installed skills are present

The managed skill assets check SHALL resolve the managed skill asset root and confirm it exists and contains at least one `SKILL.md` file (searched recursively). When the asset root does not exist, the check SHALL resolve to `Warn` instructing the user to run `mo skills install`. When the asset root exists but contains no `SKILL.md`, the check SHALL resolve to `Warn` instructing the user to run `mo skills install`. When inspecting the asset root throws, the check SHALL resolve to `Warn` surfacing the error message. Otherwise the check SHALL resolve to `Pass`.

#### Scenario: Skills present passes

- **WHEN** the managed skill asset root exists and contains at least one `SKILL.md`
- **THEN** the check SHALL resolve to `Pass`

#### Scenario: Missing asset root warns

- **WHEN** the managed skill asset root does not exist
- **THEN** the check SHALL resolve to `Warn`
- **AND** the message SHALL instruct the user to run `mo skills install`

#### Scenario: Empty asset root warns

- **WHEN** the managed skill asset root exists but contains no `SKILL.md`
- **THEN** the check SHALL resolve to `Warn`
- **AND** the message SHALL instruct the user to run `mo skills install`
