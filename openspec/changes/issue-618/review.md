# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. The prior initial-Manager-recovery finding was rechecked in the current state machine and is fixed; this round found three current must-fix problems.

## Must-Fix Findings

### 1. The initial Manager mapping is persisted after the AgentJob is submitted

`SlackManagerConversationService.LaunchSessionAsync` calls `LaunchConnectionAsync` first and only writes `SlackDmSessionMappings` afterward (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerConversationService.cs:123-144`). The launch coordinator submits the prepared job before returning (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:860-891`), and submission calls `TryAdmitAsync`, so a Runner can poll and execute while the mapping is still absent.

The Manager reply outbox rejects a valid Agent reply unless that mapping already contains the current Session (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.ManagerReplies.cs:48-68`). Therefore a fast initial Manager turn can invoke `mo slack message send` and receive `Accepted = false` before the mapping write; the reply is then lost unless the model happens to retry. A second message arriving in the same window also sees no mapping and can start another initial launch instead of being recorded as one ordinary follow-up.

This violates the issue's first acceptance criterion (the accepted request must complete with an Agent-owned reply) and the `manager-session-reply` durable-session requirement that the current mapping be persisted before dispatch, including its first-message and later-message scenarios. Establish the mapping or an equivalent durable launch fence before a Runner can execute, and add a test that holds the initial launch at the dispatch boundary while asserting that the mapping exists, the first reply is accepted, and a concurrent later message creates one follow-up rather than another initial turn.

### 2. A late duplicate follow-up can leave a fresh dual lease active until expiry

The Server issues a new Manager grant for each follow-up dispatch. If the Runner receives a late redelivery after its local operation journal is already `submitted`, `followup-handler.ts` disposes the newly created boundary and returns success at `packages/runner/src/server/followup-handler.ts:269-278`. The Server-side revocation callback is only invoked after an actual runtime completion or thrown execution (`packages/runner/src/server/followup-handler.ts:438-462`).

If the original execution's terminal event arrived before this late redelivery, no later event revokes the newly issued grant. Both hashes for the duplicate execution identity therefore remain active in the Server lease store until TTL, despite the duplicate boundary never running. This violates the `manager-execution-credentials` grant-cleanup requirement that completion, replacement, and recovery make both prior leases unusable, and the issue's short-lived execution-credential lifecycle criterion. Revoke the grant on the `submitted`/already-completed redelivery path (and cover the ordering where terminal delivery precedes the late redelivery) rather than treating disposal alone as completion.

### 3. Manager output redaction is vulnerable to credentials split across stream chunks

`ManagerExecutionBoundary.executeCli` masks each `stdout` and `stderr` data event independently before appending it (`packages/runner/src/runtime/manager-execution-boundary.ts:535-543`). `CredentialMasker` only replaces a complete registered secret (`packages/runner/src/runtime/task-log.ts:86-104`). Node stream boundaries are arbitrary, so a credential split between two data events is not matched and the concatenated plaintext is returned to the launcher/model at `manager-execution-boundary.ts:551-560`.

This violates issue Acceptance Criterion 4 and the `manager-execution-credentials` runtime-only `Credential-bearing output is produced` scenario: a plaintext credential must not reach model output, durable records, or logs. Redact using a stream-safe rolling buffer or mask the complete accumulated output before returning it, and add a test that emits a credential across multiple chunks for both stdout and stderr.

## Previous Finding Disposition

- **Initial Manager recovery from `Unknown`: fixed properly.** `AgentJobGrain` now routes the initial recovery through `RecordManagerRecoveryTurnAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:42-74`), which explicitly permits the replacement turn from unknown Session activity (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:613-623`). The Runner-loss regression records a real initial Manager turn and asserts the initial turn becomes unknown before exactly one recovery turn (`packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Grain/AgentJobManagerRunnerLossRecoverySpecs.cs:38-84`); the runtime-event coverage verifies the recovery delivery carries a fresh dual grant (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionRuntimeEventSpecs.cs:121-257`). This prior must-fix is closed.

## Dimension Checks

- **Acceptance criteria: FAIL.** Natural-language turns, reply-route ownership, current authorization, allowlisting, loop prevention, and terminal reaction convergence are present, but initial reply reliability, late duplicate lease cleanup, and output secrecy remain incomplete.
- **Coverage: FAIL.** Existing tests cover the repaired unknown-recovery path, route validation, terminal deduplication, follow-up working progress, and same-chunk redaction, but do not cover mapping-before-dispatch, late duplicate-grant cleanup, or split-chunk redaction.
- **Correctness: FAIL.** The three concrete failure paths above contradict the durable-session, runtime-only credential, and grant-cleanup contracts.
- **Consistency with surrounding code: checked, no additional criterion-level issue.** The Manager path otherwise follows the ordinary Session, reply-anchor, authorization, and outbox conventions; the remaining inconsistency is described in Finding 1.
- **Tests: checked.** The current full Server Spec assembly passed 3,182/3,182, the current Server Unit assembly passed 3,662/3,662, focused Runner boundary/follow-up/cancellation tests passed 5/5, and Runner typecheck passed. These suites do not exercise the three failing interleavings and stream-boundary cases.

## Observations

- Retired Manager parser/executor source remains present as unregistered compatibility code. Current tests confirm it is not resolved by runtime DI or terminal delivery, so this is non-blocking under the plan's explicit compatibility-source decision.
- The recovery-grant coverage manually constructs the issuer and delivery request after claiming the recovery turn; it does not exercise the complete production `AgentSessionFollowupDispatcher` grant issuance path end to end. The production wiring is still visible, but an end-to-end assertion would reduce regression risk.
- `ManagerExecutionLeaseStore.RemoveExpired` has no registered sweeper call site. Expired rows are rejected and contain only hashes/metadata, so this is cleanup debt rather than an acceptance-level failure.

**Verdict: FAIL**
<promise>FAIL</promise>