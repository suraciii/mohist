# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. The previous review was PASS after three must-fix findings were repaired. I rechecked those dispositions, then tested the current follow-up and credential lifecycle paths for regressions and missed failures.

## Must-Fix Findings

### 1. Manager credentials remain readable through a same-user runtime process

`ManagerExecutionBoundary.create` copies the complete Runner environment into `baseEnvironment` (`packages/runner/src/runtime/manager-execution-boundary.ts:119-129`). `environment()` passes that environment, plus the broker locator, to generic runtime commands (`packages/runner/src/runtime/manager-execution-boundary.ts:141-167`). When the launcher handles a request, `executeCli` puts the plaintext management or reply credential into the spawned `mo` child environment (`packages/runner/src/runtime/manager-execution-boundary.ts:387-397`).

The generated child is a same-user process. A concurrent Manager shell can inspect `/proc/*/environ` while that child is alive and consume the token in a command substitution or direct request without printing it. The `CredentialMasker` only masks captured output; it does not prevent a generic command from using a value read from the child environment. This defeats the claimed scoped-child boundary even though the direct non-launcher socket request is now rejected.

This violates issue Acceptance Criterion 4: the execution credential must be available only to the scoped Manager CLI child and must not reach a generic shell/model-visible process. It also violates `manager-execution-credentials` Runtime-only credential handling and T-003's requirement that the bearer not be in the generic shell environment. The bearer must be transferred through a boundary that a same-user model process cannot inspect or consume; adding more output masking does not fix this side channel. Add a regression test that runs a concurrent generic command during a credential-bearing child and proves it cannot use or recover the bearer through `/proc` or equivalent process inspection.

### 2. Follow-up execution leases are never revoked after the turn ends

The ordinary follow-up dispatcher issues a fresh Manager grant with a new execution identity (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:105-149`). The Runner receives it through the control request and disposes the local boundary in the completion `finally` (`packages/runner/src/server/followup-handler.ts:151-171,319-348,421-422`), but no Server completion path revokes the two lease hashes for that follow-up. The only production revocation call found for completed execution reports is `RunnerRoutes`' AgentJob report path (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:191-200`); `AgentSessionFollowupDispatcher` and the follow-up terminal delivery path never call `ManagerExecutionCapabilityIssuer.RevokeExecution`.

After a successful or cancelled follow-up, the management and reply credentials therefore remain accepted until their ten-minute expiry. The old reply credential can still post another Manager reply for the unchanged durable mapping, and the management credential can still invoke allowlisted operations after its execution has completed. This is not execution-scoped invalidation.

This violates issue Acceptance Criterion 4's execution-level credential requirement and T-003 acceptance criterion 1 / the `Manager execution grant cleanup` requirement, which require both credentials to be invalidated on completion, cancellation, replacement, recovery, and expiry. The follow-up terminal/completion lifecycle needs an idempotent Server-side revocation tied to the follow-up execution identity, including cancellation and uncertain terminal paths.

### 3. Epoch invalidation does not reach Manager follow-up boundaries

Initial AgentJob boundaries are stored in `RunnerHost.managerExecutions` (`packages/runner/src/runtime/host.ts:194,382-386,790`) and are disposed by `invalidateManagerExecutions` when the poll/heartbeat observes a new deployment epoch (`packages/runner/src/runtime/host.ts:856-875`). Manager follow-ups use a separate local `ManagerExecutionBoundary` created inside `followup-handler.ts` (`packages/runner/src/server/followup-handler.ts:151-171`). That handler is installed without any boundary registry or epoch-invalidation callback (`packages/runner/src/runtime/host.ts:281-304`).

Consequently, a follow-up that is active when the Server restarts is absent from `RunnerHost.managerExecutions`; the next epoch observation destroys only initial-work boundaries. Its broker, isolated OpenCode process, and any credential-bearing CLI child can remain alive until the follow-up happens to finish or the lease expires. This also leaves the old follow-up grant usable locally during the gap, relying only on eventual Server-side lease rejection rather than destroying the boundary as required.

This violates issue Acceptance Criterion 5 and the execution-credentials restart scenario requiring a connected Runner to discard the old grant and destroy its broker/launcher/process boundary after a Server deployment epoch change. Register every Manager execution boundary, including control-channel follow-ups, in the same invalidation and cleanup lifecycle and add an active-follow-up epoch-change test.

### 4. Manager OpenCode follow-up cancellation targets the shared runtime instead of the isolated execution

The Manager follow-up path replaces the runtime handle with an isolated OpenCode runtime created by its per-follow-up boundary (`packages/runner/src/server/followup-handler.ts:151-178`). The cancellation handler is wired with only `this.openCodeRuntime` and `this.piRuntime`, the shared host runtimes (`packages/runner/src/runtime/host.ts:301-304`). It resolves and calls cancellation against those shared handles (`packages/runner/src/server/cancel-handler.ts:174-203`); it has no lookup for the isolated follow-up boundary/runtime.

For an active Manager OpenCode follow-up, a stop request therefore cannot cancel the isolated server process. The shared runtime does not own that runtime session, so cancellation can settle as missing/not-cancellable while the Manager follow-up continues and its boundary remains alive until normal completion. The implementation cannot reliably produce the cancelled execution outcome or promptly clean up its credential/process boundary.

This violates issue Acceptance Criterion 8, which requires cancellation outcomes to close Manager liveness with one terminal reaction, and the T-004 cancellation/recovery coverage requirement. The follow-up control registry must retain the isolated Manager runtime for cancellation and dispose/revoke it on the confirmed cancellation path, with an integration test covering an active Manager OpenCode follow-up.

## Previous Finding Dispositions

1. **Direct non-launcher Manager broker requests: fixed properly.** `handleRequest` now requires `isManagerLauncherConnection` before `admitRequest` (`packages/runner/src/runtime/manager-execution-boundary.ts:337-355`), and the current integration test covers valid direct management and reply attempts with no child invocation (`packages/runner/tests/integration/manager-execution-boundary.spec.ts:97-119`). This closes the prior unauthenticated command-proxy finding, but it does not close Finding 1's separate same-user process-environment side channel.
2. **Initial token-bearing child cleanup: fixed properly.** The boundary tracks children, performs bounded SIGTERM/SIGKILL escalation, destroys sockets, closes the broker, and removes the directory afterward (`packages/runner/src/runtime/manager-execution-boundary.ts:224-264`). The bounded-disposal test remains in the integration suite. Finding 3 is a separate gap because follow-up boundaries are not in that cleanup registry.
3. **Server-detected Runner-loss Manager recovery: fixed properly.** `MarkUnknownAsync` now delivers the pending initial terminal and creates the single Manager inspection recovery transition (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:132-143`), with the existing state guard preventing duplicate recovery turns (`:29-32`). No regression was found in the current recovery path.

Earlier repairs for target authorization, shared deployment-epoch refresh, epoch-invalidated initial-job reporting, non-replaying Runner-loss dispatch, fresh follow-up grant issuance, follow-up origin routing, rejected-coordinator liveness, and platform gating were also rechecked. They do not remove the four current lifecycle findings above.

## Dimension Checks

- **Acceptance criteria: FAIL.** Acceptance Criterion 4 is violated by the same-user environment side channel and post-completion follow-up credentials. Criterion 5 is violated by missing follow-up epoch cleanup. Criterion 8 is violated by the isolated OpenCode cancellation path.
- **Coverage: FAIL.** Existing tests cover launcher peer rejection, capability catalog confinement, masking, bounded disposal, initial/follow-up grant construction, and liveness projections. No test covers `/proc` extraction/use during a credential-bearing child, follow-up lease revocation on completion/cancellation, follow-up epoch invalidation, or cancellation of an isolated Manager OpenCode turn.
- **Correctness: FAIL.** The initial Manager path is substantially repaired, but an active follow-up can retain valid credentials past completion, survive a deployment epoch change, and evade the cancellation runtime. The credential boundary and terminal lifecycle are therefore incomplete.
- **Consistency: checked, no additional issue.** Ordinary non-Manager execution continues to use its existing shared runtime and authorization paths. The failures are specific to the new Manager follow-up boundary and do not require changing the ordinary Slack Agent contract.
- **Tests: FAIL for the must-fix behavior.** `npm run test:integration -w packages/runner -- tests/integration/manager-execution-boundary.spec.ts` passed 7/7, and `npm run typecheck -w packages/runner` passed. The Server Manager credential and reply-route commands also passed, but Microsoft Testing Platform ignored the requested filters and ran the full assemblies (3,662 Unit tests and 3,179 Spec tests). None of these suites exercises the four failure cases above.

## Observations

- Manager execution is currently Linux-only because the peer-authenticated Unix-socket broker has no Windows named-pipe implementation. The issue acceptance criteria do not require cross-platform Manager execution, so this remains an observation.
- The retired `SlackManagerToolTurnProcessor`, `SlackManagerToolExecutor`, and execution-fence types remain as unregistered compatibility source. The current runtime does not resolve them; this matches the migration artifact's temporary source-retention allowance.
- `AgentSessionGrain.SlackDelivery` still extracts `assistantText` for follow-up delivery events (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.SlackDelivery.cs:58`), although the current terminal handler ignores it. This is unnecessary retired-protocol surface but does not add another verdict-changing finding.
- `ManagerExecutionLeaseStore.RemoveExpired` exists, but no expiry sweeper registration was found. Validation rejects expired values, so this is cleanup debt rather than a separate acceptance failure.

**Verdict: FAIL**
<promise>FAIL</promise>
