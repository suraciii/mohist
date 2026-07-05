### Requirement: Network-bound runner commands run under a single default per-command timeout

Network-bound runner commands SHALL run under one single default per-command timeout (approximately 120s), applied via the `command-timeout` primitive. The network commands in scope are: `git clone`, `git fetch`, `git ls-remote`, and `git push` in `runtime/workspace.ts` and the delivery actions; and the `gh pr` / `gh` API calls (`gh pr list` / `edit` / `create`, plus `gh auth status` / `gh --version` precheck and other `gh` network invocations) in the delivery actions (`push.ts`, `rebase.ts`, `create-github-pr.ts`, `mark-github-pr-ready.ts`, `merge-github-pr.ts`, `github-pr-status.ts`). There SHALL NOT be a per-command-type timeout budget table; one default value SHALL apply to all network commands.

#### Scenario: git clone runs under the network default timeout

- **WHEN** `WorkspaceManager` performs a fresh `git clone` (the `cloneFresh` path)
- **THEN** the clone SHALL run with the network default `timeoutMs`

#### Scenario: git ls-remote base-branch verification runs under the network default timeout

- **WHEN** `WorkspaceManager.verifyBaseBranch` runs `git ls-remote` against the remote
- **THEN** the command SHALL run with the network default `timeoutMs`

#### Scenario: git fetch of the base runs under the network default timeout

- **WHEN** a delivery action fetches the base branch (e.g. `rebase.ts` `git fetch <remote> <baseBranch>`, `create-github-pr.ts` `git fetch` of the base)
- **THEN** the fetch SHALL run with the network default `timeoutMs`

#### Scenario: git push runs under the network default timeout

- **WHEN** a delivery action performs a `git push` (e.g. `push.ts`, `create-github-pr.ts` `push --force-with-lease`)
- **THEN** the push SHALL run with the network default `timeoutMs`

#### Scenario: gh API calls run under the network default timeout

- **WHEN** a delivery action invokes a `gh` subcommand that hits the network (`gh pr list` / `edit` / `create`, and other `gh` network calls)
- **THEN** the call SHALL run with the network default `timeoutMs`

#### Scenario: One default value covers all network commands

- **WHEN** the network default timeout is configured
- **THEN** a single value SHALL apply to every network command in scope
- **AND** no per-command-type (clone vs fetch vs push vs gh) timeout budget table SHALL exist

### Requirement: A network timeout surfaces as a structured failure carrying step name, command summary, and duration

When a network command exceeds its per-command timeout, the delivery action SHALL surface a structured failure whose information identifies what hung and for how long. The failure information SHALL include the **step name** (the action's phase label, e.g. `git-fetch-base`, `gh-pr-create`, `git-push`), a **command summary** (the command and key arguments, not raw secrets), and the **timeout duration** that elapsed. This structured information SHALL flow into the action's existing result output so downstream renderers (CLI delivery-failure guidance, web delivery-failure view) and the task log can show which command stalled.

#### Scenario: A timed-out network command records which step hung

- **WHEN** a network command in a delivery action exceeds the network default timeout
- **THEN** the surfaced failure SHALL carry the step name of the phase that hung (e.g. `gh-pr-create`, `git-fetch-base`)
- **AND** SHALL NOT be indistinguishable from an arbitrary non-zero exit

#### Scenario: A timed-out network command records a command summary

- **WHEN** a network command exceeds the network default timeout
- **THEN** the surfaced failure SHALL carry a command summary identifying the command and its key arguments
- **AND** SHALL NOT include secrets or credentials in the summary

#### Scenario: A timed-out network command records the elapsed timeout duration

- **WHEN** a network command exceeds the network default timeout
- **THEN** the surfaced failure SHALL carry the timeout duration that was applied (e.g. `120s`)
- **AND** the operator SHALL be able to tell the failure was a per-command timeout, not a work-level abort

### Requirement: A network command timeout classifies as retry-safe through the existing classification path

A network command timeout SHALL be classified as **`retry-safe`** via the existing `classifyGhFailure` / `classifyPushFailure` path in `github-pr-classify.ts`. The classification rules SHALL NOT be changed; the structured timeout output SHALL be matched by the existing `retry-safe` arm (which already matches `timeout` / `timed out`). A network timeout SHALL NOT be misclassified as `base-moved`, `protection-conflict`, `pr-state-conflict`, or `config-error`.

#### Scenario: A network timeout classifies retry-safe

- **WHEN** a network command (push, fetch, gh pr create, etc.) times out and its output is fed to `classifyGhFailure` / `classifyPushFailure`
- **THEN** the result SHALL be `retry-safe`
- **AND** SHALL NOT be `base-moved`, `protection-conflict`, `pr-state-conflict`, or `config-error`

#### Scenario: No new classification rule is added

- **WHEN** the classification table is inspected after this change
- **THEN** no new `GitHubPrErrorCode` arm SHALL be introduced to handle timeouts
- **AND** the existing `retry-safe` matching SHALL be the sole path that absorbs the structured timeout output

### Requirement: Local-only git commands are excluded from the per-command timeout

Local-only git commands — those that do not contact the network and therefore cannot hang on a remote — SHALL NOT carry a per-command timeout. They SHALL continue to run only under the work-level `AbortSignal` as they do today. The local-only set includes at least: `rev-parse`, `status`, `checkout`, `diff`, `merge-base`, `reset`, `rebase` (local and `--abort`), `merge --abort`, `cherry-pick --abort`, `cat-file`, `ls-tree`, `show-ref`, `fsck`, `branch`, `add`, `commit`, and `remote get-url`. This requirement draws the boundary of the network-command policy so that the per-command timeout is applied only where a hang is plausible.

#### Scenario: Local rev-parse / status probes keep no per-command timeout

- **WHEN** a delivery action runs a local git probe (`rev-parse`, `merge-base`, `status`, `diff`, `checkout`)
- **THEN** the command SHALL run under the work-level signal only
- **AND** SHALL NOT be passed a per-command `timeoutMs`

#### Scenario: Local rebase / reset / commit keep no per-command timeout

- **WHEN** a delivery action runs a local rebase, `reset`, `add`, or `commit`
- **THEN** the command SHALL run under the work-level signal only
- **AND** SHALL NOT be passed a per-command `timeoutMs`

#### Scenario: Repository-integrity probes keep no per-command timeout

- **WHEN** a cache-integrity probe runs `fsck`, `cat-file`, `ls-tree`, or `show-ref`
- **THEN** the command SHALL run under the work-level signal only
- **AND** SHALL NOT be passed a per-command `timeoutMs`

### Requirement: Network timeout policy is verifiable through fake subprocess seams without real network

Tests for the network-command timeout policy SHALL use the existing injection seams (`setXxxGitRunnerForTest`, `setGitHubPrGhRunnerForTest`, `setPushGitRunnerForTest`, `setRebaseGitRunnerForTest`) to simulate a hung network command, drive the timeout via a fake timer, and assert the structured result, step/summary/duration fields, and `retry-safe` classification. Tests SHALL NOT depend on real network, real `git`/`gh` processes, or wall-clock timing. Local-command call sites SHALL be asserted to remain on the work-level signal only (no per-command timeout passed).

#### Scenario: A hung network command is simulated via the injection seam

- **WHEN** a test injects a git/gh runner that never resolves for a network call site and advances the fake timer past the network default
- **THEN** the action SHALL surface a structured timeout failure classified `retry-safe`
- **AND** no real network call SHALL be made

#### Scenario: Local call sites are asserted to carry no per-command timeout

- **WHEN** the test harness inspects the local-only git call sites
- **THEN** each local-only call SHALL be invoked with no per-command `timeoutMs`
- **AND** only the network call sites SHALL carry the network default `timeoutMs`
