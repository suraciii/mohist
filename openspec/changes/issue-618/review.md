# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. I verified the four findings from the previous review and traced the new Manager follow-up expiry path through durable Session recovery, follow-up dispatch, credential issuance, and tests.

## Must-Fix Findings

### 1. Expiry recovery is persisted but never dispatched, so it never receives a fresh credential

`RunnerRoutes` correctly classifies a Manager follow-up expiry event, revokes the old execution lease, calls `EnsureManagerCredentialExpiryRecoveryAsync`, and then invokes the ordinary follow-up dispatcher (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:725-732`). However, `EnsureManagerCredentialExpiryRecoveryAsync` creates the recovery input and queued turn directly through `RecordManagerRecoveryTurn` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:3527-3542`). That transition does not create an `AgentSessionFollowupLease` in `PendingFollowups`.

The only ordinary follow-up dispatcher entry point, `BeginNextFollowupDispatchAsync`, requires a queued turn to have a matching pending lease and returns `null` when it does not (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.FollowupDispatch.cs:16-20`). Consequently, the subsequent `DispatchNextAsync` call finds the queued `manager-recovery-turn` but cannot dispatch it (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:56-61`). No Runner request is emitted, `IssueManagerGrantAsync` is never reached, and the recovery Agent never receives fresh management and reply credentials.

This violates issue Acceptance Criterion 5: after credential expiry, Manager execution must reauthorize from the immutable Slack origin and receive a new credential rather than continue as the completed old execution. It also violates the `manager-execution-credentials` expiry scenarios and T-003 acceptance criteria requiring exactly one expiry-recovery/new-turn transition with fresh credentials. The focused Server tests only assert that a queued recovery turn exists (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionRuntimeEventSpecs.cs:98-104`); they do not assert that the dispatcher claims it, sends a fresh grant, or starts the recovery runtime. Make the recovery transition use the same durable follow-up lease/dispatch contract (or an equivalent durable recovery dispatcher), and add a test that observes a fresh grant and one actual recovery dispatch.

## Previous Finding Dispositions

1. **Same-user credential environment exposure: fixed properly.** `ManagerExecutionBoundary` removes inherited Manager bearer variables, and the credential-bearing `mo` request is handled through the Runner-owned credential proxy. The integration coverage inspects the live child environment and verifies both bearer values are absent. The latest expiry changes do not alter that boundary.
2. **Follow-up leases surviving completion: fixed properly for completion, cancellation, and rejection.** Server terminal delivery and Runner dispatch/completion/cancellation paths still revoke the follow-up execution identity, invalidating both lease hashes. The new expiry event also revokes the old execution before attempting recovery.
3. **Follow-up boundaries missing epoch invalidation: fixed properly.** Initial jobs and control-channel follow-ups remain registered in the shared Manager execution registry, and heartbeat/poll epoch changes invalidate the registered boundaries. The new recovery code does not bypass that registry for dispatched work; its defect is that it is never dispatched.
4. **OpenCode follow-up cancellation using the shared runtime: fixed properly.** Follow-up entries retain their isolated runtime handle and cancellation resolves that handle rather than the shared runtime. The latest changes do not regress this behavior.

## Dimension Checks

- **Acceptance criteria: FAIL.** Criteria 1-4 and 6-8 remain covered by the previously reviewed implementation. Criterion 5 remains incomplete: the new expiry transition records a queued Session turn but does not execute a fresh recovery turn or issue fresh credentials.
- **Coverage: FAIL for the must-fix behavior.** The new Runner tests cover successful and failed follow-up classification after expiry, and the Server tests cover persistence and idempotency of the queued recovery record. No test covers the required end-to-end handoff from expiry event to follow-up lease, grant issuance, Runner delivery, and fresh recovery execution.
- **Correctness: FAIL.** The production control flow calls the dispatcher immediately after recording recovery, but the dispatcher is lease-driven and cannot claim this turn. The implementation therefore stops at durable bookkeeping rather than satisfying the required recovery behavior.
- **Consistency with the surrounding codebase: checked, no additional issue.** The intended fresh-grant path already exists in `AgentSessionFollowupDispatcher.IssueManagerGrantAsync`; the defect is the new recovery transition not entering the existing follow-up lease contract. The four earlier boundary and cancellation repairs remain consistent with local conventions.
- **Tests: checked.** `npm run test:run -w packages/runner -- src/server/followup-handler.test.ts` passed 4/4. The serial Server spec invocation passed 3,181/3,181, but the Microsoft Testing Platform ignored the requested test filter and ran the full assembly. These tests do not exercise actual recovery dispatch, which is the gap above.

## Observations

- The initial Manager recovery helper in `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:44-67` also records a recovery turn through `RecordFollowupTurnAsync`; any future review of initial-turn expiry/restart recovery should verify that path uses a dispatchable follow-up lease as well. The finding above is already independently blocking for the current follow-up expiry path.
- The issue-618 plan leaves exact credential TTL and clock-skew policy open in `design.md`; this remains non-blocking because the required invalidation and fresh-credential lifecycle is specified.
- The plan retains retired execution-fence source/schema as an unregistered compatibility artifact. That does not restore the retired Manager model protocol and remains outside the acceptance criteria.

**Verdict: FAIL**
<promise>FAIL</promise>
