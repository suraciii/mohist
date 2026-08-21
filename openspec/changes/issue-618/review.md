# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. The three must-fix findings from the previous review were rechecked: mapping-before-dispatch, late duplicate-grant revocation, and split-chunk output masking are fixed in the current tree. This round found three remaining must-fix problems.

## Must-Fix Findings

### 1. An in-flight replay of the original message creates a second Session input

`SlackManagerIngressService.AcceptAsync` accepts an existing inbox row, but returns `Duplicate` only when that row already has a non-empty `RouteSessionId` (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:165-184`). The Manager launch path persists the conversation mapping before `LaunchConnectionAsync`, but it does not populate the inbox route until after `ProcessAsync` returns (`SlackManagerConversationService.cs:127-163`, then `SlackManagerIngressService.cs:203-218`).

Therefore, if Slack replays the original message while the initial launch is blocked between inbox acceptance and route stamping, the replay has `AlreadyExisted = true` and `route.SessionId = null`, bypasses the duplicate return, and calls `ProcessAsync` again. That method finds the pre-published mapping, `IsPendingInitialLaunchAsync` also sees no inbox route, and `AcceptFollowupAsync` queues a new follow-up using `slack:{origin}` as a different idempotency key from the initial launch coordinator's `slack:{workspace}/{conversation}/{message}` key. The original message can consequently produce two inputs/turns and two management executions.

This violates the `manager-session-reply` replay scenario and T-001's requirement that replayed workspace/conversation/message identities create no second Session input, turn, or dispatch. It also violates the `manager-reaction-liveness` replay scenario because the duplicate can start another logical execution. Treat an already-existing inbox identity as the existing acceptance even while its route fence is null, while preserving the separate later-message path, and add a test that blocks the initial launch then replays the same message and asserts exactly one input, turn, job, and execution.

### 2. Generic Manager tools can bypass the Manager capability boundary with ordinary local credentials

`ManagerExecutionBoundary` copies the Runner process environment into `baseEnvironment` and only removes the two Manager bearer variables (`packages/runner/src/runtime/manager-execution-boundary.ts:131-171`). Its Pi Bash integration permits arbitrary shell commands through `bash -lc` (`manager-execution-boundary.ts:181-196`), and it does not restrict filesystem reads. The real CLI's normal credential resolver explicitly falls back to `MOHIST_TOKEN`, `MOHIST_ADMIN_TOKEN`, `MOHIST_ADMIN_TOKEN_PATH`, and `~/.mohist/admin-token` (`packages/cli/Mohist.Cli/CliCredentialProvider.cs:4-12`, `75-96`).

A Manager model can therefore invoke the real `mo` executable directly with `MOHIST_MANAGER_MODE=0`, or read the local admin token and use `curl`/another HTTP client, instead of using the generated Manager launcher. On the normal local-server default (`packages/cli/Mohist.Cli/Program.cs:10-13`), the machine-local admin token is accepted for loopback requests. This permits unlisted CLI commands and direct management API calls outside the catalog, including destructive operations, whenever the Runner account has the ordinary CLI credential that this code supports. The current boundary tests only prove that Manager bearer values are absent from the child environment; they do not test an actual ordinary credential or direct invocation of the real CLI.

This violates issue Acceptance Criterion 7 and the `manager-cli-capabilities` requirement that unlisted CLI commands and direct management API calls be unavailable to Manager execution. It also violates the execution-credentials requirement that an ordinary operator credential cannot substitute for the scoped Manager credential. Remove broader credentials and sensitive credential files from the Manager process/filesystem boundary or enforce an equivalent OS-level sandbox, prevent direct execution of the real CLI outside the broker, and add an integration test with a real local credential proving that generic shell, direct CLI, and direct HTTP paths cannot perform Manager operations.

### 3. Server-side Runner-loss recovery does not revoke the interrupted execution leases

When the Server detects a lost Runner, `RunnerGrain.CloseoutLostAsync` only calls `IAgentJobGrain.MarkUnknownAsync` (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:848-878`). The Manager-specific recovery creates a fresh inspection turn (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:29-74`), but neither path calls `ManagerExecutionCapabilityIssuer.RevokeWork` or `RevokeExecution`. The only AgentJob report-path revocation is in `RunnerRoutes.cs:193-202`, which cannot run after the Runner is lost.

The old execution identity remains active in the lease store until expiry while the replacement recovery execution receives a different identity and fresh credentials. Thus an intercepted or otherwise retained management or reply bearer from the interrupted turn remains usable during recovery, contrary to the required recovery boundary.

This violates the `manager-execution-credentials` grant-cleanup requirement and its recovered-execution scenario, which require both prior leases to become unusable on recovery, as well as the issue's restart/session-recovery credential lifecycle criterion. Revoke all leases for the lost AgentJob/work identity as part of the durable unknown transition before issuing the recovery turn, and add a test that simulates Runner loss and rejects both old credentials immediately, before TTL and before the fresh recovery grant is used.

## Previous Finding Dispositions

- **Initial Manager recovery from `Unknown`: fixed properly.** The Manager-only recovery transition is now used for the initial turn, and the current Runner-loss specs cover one unknown initial turn followed by exactly one recovery turn with fresh grants.
- **Mapping before initial dispatch: fixed for the originally reported ordering.** `SlackManagerConversationService` writes the deterministic mapping before `LaunchConnectionAsync`, and the current dispatch-boundary regression proves a concurrent later message queues in the original Session. The in-flight replay case in Finding 1 is a distinct gap because the inbox route remains unset until after the first coordinator call returns.
- **Late duplicate follow-up grant cleanup: fixed properly.** The `submitted` operation-journal path now disposes the unused boundary and invokes `onManagerExecutionFinished` (`packages/runner/src/server/followup-handler.ts:269-278`).
- **Split-chunk credential masking: fixed properly.** Manager stdout and stderr are accumulated and masked after the complete stream is received (`packages/runner/src/runtime/manager-execution-boundary.ts:535-560`), with a current split-chunk integration test.

## Dimension Checks

- **Acceptance criteria: FAIL.** Natural-language turns, Agent-owned replies, loop prevention, current route authorization, allowlisting for the broker path, and terminal reaction convergence are present, but replay idempotency and the effective CLI capability boundary fail in the paths above; Runner-loss recovery also leaves old credentials active.
- **Coverage: FAIL.** The current tests cover the repaired initial mapping race, duplicate submitted follow-ups, split-chunk masking, reply ownership, terminal deduplication, and fresh recovery grant issuance. They do not cover replay before inbox route stamping, ordinary local credential access from generic Manager tools, or lease revocation on Server-detected Runner loss.
- **Correctness: FAIL.** The three findings contradict the durable replay, explicit capability allowlist, and execution-credential cleanup contracts in the issue plan/spec artifacts.
- **Consistency with surrounding codebase and conventions: checked, no additional criterion-level issue.** The ordinary Session, Slack anchor, outbox, and route patterns are otherwise followed; the remaining inconsistencies are the three boundaries described above.
- **Tests: checked.** Focused Runner tests passed 5/5, Manager boundary integration tests passed 9/9, the Server Spec assembly passed 3,183/3,183, the CLI assembly passed 1,950/1,950, the Server Unit assembly passed 3,662/3,662, Runner typecheck passed, and `git diff --check` was clean. Those passing suites do not exercise the three failing cases.

## Observations

- The retired Manager parser/executor remains in source but is unregistered; current runtime resolution and terminal delivery do not use it. This is non-blocking under the plan's explicit compatibility-source decision.
- `ManagerExecutionBoundary.handleCredentialRequest` accepts an arbitrary `http(s)` URL and injects the selected Manager bearer into it (`manager-execution-boundary.ts:443-505`). The supported Manager CLI currently uses its fixed process base URL, so this was recorded as a security-hardening observation rather than an additional verdict-changing finding; pin the proxy to the configured Server origin and add a negative destination test when repairing the credential boundary.
- `ManagerExecutionLeaseStore.RemoveExpired` still has no visible production sweeper call site. Expired credentials are rejected and the retained store rows contain only hashes and metadata, so this remains cleanup debt rather than an additional acceptance-level failure.

**Verdict: FAIL**
<promise>FAIL</promise>
