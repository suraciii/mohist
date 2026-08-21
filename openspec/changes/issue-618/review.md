# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 specs/design. The prior review was FAIL with four must-fix findings. I rechecked each disposition and the follow-up repairs before evaluating regressions.

## Must-Fix Findings

### 1. The repaired broker is an unauthenticated Manager command proxy

`ManagerExecutionBoundary.environment()` deliberately gives every Manager runtime process the broker locator (`packages/runner/src/runtime/manager-execution-boundary.ts:91-95`). The generated `mo` launcher sends `{ kind, args, cwd }` to that socket (`packages/runner/src/runtime/manager-execution-boundary.ts:189-208`), but `handleConnection` accepts the same request shape from any client and immediately calls `executeCli` (`packages/runner/src/runtime/manager-execution-boundary.ts:244-266`). `executeCli` then starts the real `mo` with the management or reply credential in its environment (`packages/runner/src/runtime/manager-execution-boundary.ts:270-282`).

A generic model shell that inherits `MOHIST_MANAGER_BROKER` can therefore connect directly and submit valid `mo` arguments, including the reply or management operation kind, without proving that the request came from the generated launcher. The bearer is not returned as JSON anymore, but the generic shell can still cause the credential-bearing child to perform the privileged operation and receive its result. This is an execution-boundary bypass, not merely a token-oracle concern. The test only submits a malformed old request with no `args` and therefore never exercises a valid direct proxy request (`packages/runner/src/runtime/manager-execution-boundary.test.ts:26-35,71-95`).

This violates issue acceptance criterion 4 and the stronger `manager-execution-credentials`/T-003 requirement that the grant be exposed only to the scoped Manager CLI child process and that generic model shell commands cannot use the capability. The broker needs an unforgeable launcher/process authorization boundary or another design that makes direct socket requests unable to perform Manager operations.

### 2. Boundary disposal does not terminate token-bearing CLI children

`dispose()` shuts down the isolated OpenCode runtime, closes the broker, and removes the directory, but it does not track or terminate children spawned by `executeCli` (`packages/runner/src/runtime/manager-execution-boundary.ts:171-185,270-299`). The broker's socket remains open until the child closes it, so `server.close` can wait indefinitely for an in-flight CLI child. More importantly, a cancelled, expired, or epoch-invalidated Manager execution can leave the `mo` child alive with the plaintext credential in its environment; `rm(this.directory)` is reached only after the broker close and does not kill that process tree.

`RunnerHost.invalidateManagerExecutions()` invokes this disposal path during epoch changes (`packages/runner/src/runtime/host.ts:861-869`). Thus the implementation does not meet the required cleanup and lifetime boundary for credentials and process trees. This violates issue acceptance criterion 4 and T-003's execution-lifecycle requirement covering expiry, cancellation, epoch change, broker cleanup, and destruction of the isolated process tree. The boundary must retain child/process handles and terminate them, with bounded cleanup, before releasing the execution.

### 3. Runner-loss suppression leaves Manager recovery without a fresh turn

When the Runner is detected offline, `RunnerGrain.CloseoutLostAsync` calls `IAgentJobGrain.MarkUnknownAsync(..., recoveryDeadlineAt)` directly (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:864-877`). Both `MarkUnknownAsync` overloads only call `EnterUnknownStateAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:126-133`); they do not call `EnsureManagerRecoveryAsync`. That recovery helper is reached for an unknown result reported by the Runner and for the local timeout path, but not for this server-side Runner-loss transition (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:81-124`). The unknown-state reminder only delivers the initial terminal status or eventually fails the recovery deadline; it does not create the Manager recovery follow-up (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Reminders.cs:48-67`).

The subsequent dispatch repair intentionally suppresses an unknown Manager dispatch to prevent replay of an uncertain natural-language mutation (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:181-201`). That prevents the dangerous replay, but because no recovery turn was created, the Manager receives neither the required inspection-only transition nor a fresh capability grant and eventually remains unknown until failure.

This is the prior review's Runner-loss/replay finding only partially fixed: replay is now suppressed, but recovery is missing. It violates issue acceptance criterion 5 and T-003's requirement to recover from the immutable Slack origin with exactly one fresh, non-replaying execution after Runner loss/recovery.

## Previous Finding Dispositions

1. **Forged launcher PID/path broker:** the old direct bearer-return token oracle was removed, but the replacement command proxy is callable by any process with the inherited locator. Finding 1 remains a must-fix boundary failure in the current implementation.
2. **Process-local deployment epoch:** fixed properly. The shared-store implementation refreshes the epoch on each `Current`/`Available` read, and issue/validation paths capture and compare the refreshed value (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:224-247,575-624,644-720`). The cross-instance epoch test passes.
3. **Epoch invalidation without a durable unknown report:** fixed for the normal abort path. Invalidated in-flight work is marked, aborted, converted to `manager_epoch_changed`/unknown, journaled, and reported (`packages/runner/src/runtime/host.ts:861-869`; `packages/runner/src/runtime/host-execution.ts:287-301`). Finding 2 still means disposal can prevent this transition from completing for a hung CLI child.
4. **Runner-loss replay of the original Manager prompt:** the original dispatch is no longer replayed, but the fresh recovery transition is not created, as described in Finding 3. The must-fix disposition does not hold.

## Dimension Checks

- **Issue acceptance criteria: FAIL.** Criteria 1, 2, 3, 6, 7, and 8 remain covered with no new must-fix problem found in this round. Criterion 4 is violated by the unauthenticated broker and incomplete child lifetime cleanup. Criterion 5 is violated by the missing Runner-loss recovery transition.
- **Coverage: FAIL.** Initial/follow-up Session routing, reply ownership, allowlist admission, current authorization, liveness convergence, shared epoch refresh, and epoch-invalidated unknown reporting are covered. There is no focused test for a valid direct broker request, child termination on disposal, or Manager Runner-loss recovery.
- **Correctness: FAIL.** The natural-language Manager and terminal reply paths remain correct in the reviewed scenarios, but the runtime capability boundary can be bypassed and one recovery path suppresses work without creating the promised fresh turn.
- **Consistency: checked, no new issue.** The repaired paths continue to use the ordinary Session, follow-up, CLI admission, Slack anchor, and liveness primitives. The missing Manager recovery call is a lifecycle inconsistency between Runner-reported unknown and server-detected Runner loss.
- **Tests: FAIL for the must-fix behavior.** `npm run test:run -w packages/runner -- src/runtime/manager-execution-boundary.test.ts` passed 2/2. The Server Unit assembly passed 3,121/3,121 and the Server Spec assembly passed 3,709/3,709, but the Microsoft test platform ignored the requested class filters and ran each full assembly; these suites contain no cases for the direct proxy attack, child-process cleanup, or Manager-specific Runner-loss recovery.

## Observations

- `AgentSessionGrain.SlackDelivery` still extracts `assistantText` into follow-up delivery events even though current Manager terminal handling no longer consumes it. This is unnecessary contract surface but does not currently author a Slack reply.
- The retired `SlackManagerToolTurnProcessor`, `SlackManagerToolExecutor`, and execution-fence source remain present but unregistered. This matches the migration plan's temporary compatibility-source allowance.
- `ManagerExecutionLeaseStore.RemoveExpired` exists, but no expiry sweeper registration was found. Validation rejects expired leases, so this remains cleanup and observability debt rather than a separate acceptance failure.

**Verdict: FAIL**
<promise>FAIL</promise>
