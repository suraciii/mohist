# Review: Issue 618

Review round: re-review of the current product change at `bd18e9720` against the live issue acceptance criteria and the issue-618 specs/design. The previous review was a FAIL with eight findings; the repaired paths were checked before evaluating regressions.

## Must-Fix Findings

### 1. The Manager broker can still be forged by a generic same-user process

`ManagerExecutionBoundary.environment()` exposes the broker path and the private launcher directory to every Manager runtime process (`packages/runner/src/runtime/manager-execution-boundary.ts:80-89,181-205`). The broker grants a bearer when the request includes a `launcherPid` whose `/proc/<pid>/cmdline` contains the generated `mo` path (`packages/runner/src/runtime/manager-execution-boundary.ts:232-273`). This is only a pathname/PID assertion, not an unforgeable launcher-only boundary.

A generic model shell runs as the same Runner user and can read the inherited broker locator, write or replace the mode-0700 launcher in that same-user directory, and run a helper at that path with a matching command line before requesting the broker. It can then receive the plaintext management or reply credential directly. The same-user boundary also permits racing a live legitimate launcher PID. The test only checks a socket request with no PID and therefore does not exercise this attack (`packages/runner/src/runtime/manager-execution-boundary.test.ts:22-31`).

This violates Acceptance Criterion 4: a credential must be available only to the scoped `mo` child and must not be exposed to a generic shell or model-visible process. The process boundary must authenticate the generated launcher in a way a same-user generic command cannot forge, or move bearer retrieval into a boundary the generic command cannot access; checking a path in `/proc` is insufficient.

### 2. The shared deployment epoch is cached per Server process, so restart invalidation is not authoritative

Production registration does use a shared SQLite lease store (`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:226-233`), and `AdvanceDeploymentEpoch()` updates the shared epoch row and revokes leases (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:281-305`). However, `ManagerDeploymentEpoch.Current` is a process-local `_current` value (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:128-166,170-194`). `ManagerExecutionCapabilityIssuer.Issue()` and both validation paths compare against that cached value and never read the shared epoch on each operation (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:541-584,601-630,639-675`).

If Server A is running at epoch E1 and Server B starts at E2, Server A still believes E1 is current. It can issue new E1 leases after B has advanced the shared epoch, and it will accept those leases locally while Server B rejects them. A lease issued by B is likewise rejected by A because A compares it with its stale local E1. This breaks the single authoritative restart boundary and can prevent a recovered Manager execution from obtaining a grant.

This violates Acceptance Criterion 5, which requires restart/session recovery to reauthorize from the immutable origin and issue a valid fresh credential. The shared epoch must be read or atomically validated through one authoritative provider on every issue/validation path; a shared lease table with stale per-process epoch caches is not sufficient.

### 3. Epoch invalidation aborts Manager work without recording a durable recovery transition

When a Runner observes a changed epoch, `RunnerHost.pollOnce()` calls `invalidateManagerExecutions()` (`packages/runner/src/runtime/host.ts:850-867`). That method aborts the local controllers and disposes the Manager boundaries, but it does not remove the corresponding `inFlight` entries or report an unknown/recoverable outcome. If execution observes the abort as an exception, `executeAndTransitionCore()` returns immediately on `signal.aborted` (`packages/runner/src/runtime/host-execution.ts:239-281`), leaving the stale key in the next poll report.

The Server then rejects the old-epoch poll round once, but subsequent polls still report the old work key. `DispatchService` counts running AgentJob work as active and skips redelivery when that key is reported (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:80-112,181-198`). The Manager job can therefore remain running indefinitely with no terminal event or fresh execution. If the runtime instead returns a normal failure after the abort, it is reported as an ordinary failed job rather than entering the Manager origin-based recovery path.

This violates Acceptance Criterion 5: a Server restart must invalidate the current execution and allow durable recovery from the immutable Slack origin with a fresh credential. Epoch change needs an explicit durable unknown/recovery transition and local work reconciliation; disposing the process boundary alone leaves the job either stranded or incorrectly terminalized.

### 4. Runner-loss recovery can replay an uncertain Manager prompt and its state-changing CLI call

The generic AgentJob recovery path treats an unknown job as recoverable when its failure reason is `runner-lost` (`packages/server/src/Mohist.Server/Infrastructure/Data/AgentJobs/AgentJobStore.cs:430-449,513-528`). `DispatchService` then deserializes the original persisted `DispatchJson` and redelivers it (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:181-198,569-577`). The Runner admits that work through the normal execution path (`packages/runner/src/runtime/host.ts:784-795`), so the original prompt and Manager CLI intent are executed again with a new grant rather than being classified as an uncertain operation requiring inspection.

A Manager turn can lose its process after a CLI mutation reaches the Server but before the result is observed. Replaying the original natural-language prompt can repeat that mutation. This violates Acceptance Criterion 5's recovery requirement and the issue-618 `manager-execution-credentials` recovery scenario requiring an uncertain state-changing operation to be marked unknown without automatic replay. Recovery must carry a non-replaying inspection/recovery transition, not reuse the original Manager prompt as executable work.

## Previous Finding Dispositions

1. **Cross-target Manager authorization: fixed for the reviewed routes.** Manager admission now reauthenticates the current actor and checks the workspace, project, and routed Connection before endpoint execution (`packages/server/src/Mohist.Server/Auth/Identity/ManagerCapabilityAdmissionMiddleware.cs:54-123`); the Agent body path also checks the selected Agent (`packages/server/src/Mohist.Server/Api/SlackManagerRoutes.cs:26-75`). This addresses the previous cross-workspace/project failure without changing unmarked operator/Web requests.
2. **Generic-shell credential extraction: not fixed.** The launcher PID/path check is the same-user-forgeable broker boundary described in Finding 1.
3. **Follow-up grants and scoped execution: fixed for the implemented Pi/OpenCode paths.** Follow-up dispatch now issues a fresh grant after current actor/Enrollment authorization and carries it in `FollowupParams` (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:61-153`, `packages/server/src/Mohist.Server/Contracts/RunnerControlContracts.cs:77-87`). The Runner creates a per-follow-up boundary, uses the isolated OpenCode runtime, and passes the Manager boundary into Pi (`packages/runner/src/server/followup-handler.ts:148-178,294-313`, `packages/runner/src/server/command-runtime.ts:270-302`).
4. **Credential-expiry transition: fixed for the explicit expiry result, but restart/recovery remains incomplete.** `manager-credential-expired` now records an unknown outcome and creates one durable, non-replaying inspection follow-up (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:17-65`). Findings 3 and 4 show that epoch changes and Runner-loss recovery still do not satisfy the broader restart/recovery contract.
5. **Follow-up terminal origin: fixed.** Follow-up delivery now takes the triggering input provenance and carries its message identity (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.SlackDelivery.cs:16-42`), and terminal handling derives the Manager liveness key from that source (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:36-50`).
6. **Rejected coordinator liveness: fixed.** Ingress now finalizes the durable receipt with an unknown terminal reaction before marking a rejected inbox row dispatched (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:185-209`).
7. **Unsupported Runner gating: fixed for platform and shared-runtime readiness.** Windows removes Manager capabilities at registration and the host removes them when its shared OpenCode runtime is not ready (`packages/runner/src/runtime/registration-state.ts:25-43`, `packages/runner/src/runtime/host-helpers.ts:28-49`).
8. **Shared lease store/restart epoch: not fully fixed.** The production lease hashes are now in shared SQLite, but the epoch used by issue and validation remains process-local, as described in Finding 2. The local Runner invalidation/recovery gap is described in Finding 3.

## Dimension Checks

- Issue acceptance criteria: checked adversarially. Criteria 1, 2, 3, 6, 7, and 8 have implementation coverage with no additional must-fix problem found in this round. Criteria 4 and 5 are violated by Findings 1-4.
- Coverage: checked. Initial and follow-up Sessions, Agent-owned reply routing, loop prevention, allowlist admission, target checks, current actor/Enrollment checks, liveness convergence, explicit expiry handling, and platform gating are present. Shared-epoch behavior, forged launcher access, epoch-abort recovery, and non-replaying Runner-loss recovery are not covered by the passing tests.
- Correctness: checked with failure cases for a same-user broker client, two Server instances advancing one epoch, a Runner observing an epoch change while Manager work is in flight, and an unknown Manager mutation after Runner loss. Each produces the broken behavior described above.
- Consistency: checked. The repaired paths reuse ordinary Session and Slack liveness primitives, but the Manager credential boundary and recovery semantics do not yet provide the isolation and durable state transitions required by the issue-618 design.
- Tests: checked. Runner boundary/context tests passed 17/17 and Runner typecheck passed. The requested Server filters were ignored by the Microsoft Testing Platform; the resulting full Server Unit suite passed 3,120/3,120 and the full Server Spec suite passed 3,709/3,709. Those suites contain no cases for same-user broker forgery, cross-instance epoch freshness, epoch-abort recovery, or non-replaying Manager runner-loss recovery.

## Observations

- `AgentSessionGrain.SlackDelivery` still extracts and includes `assistantText` in follow-up delivery events (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.SlackDelivery.cs:58`), even though Manager terminal handling no longer consumes it. This does not currently author a Slack reply, but it leaves unnecessary model-output extraction in the Manager event contract.
- The retired `SlackManagerToolTurnProcessor`, `SlackManagerToolExecutor`, and execution-fence source remain present but are not registered or reached by the current Manager ingress/terminal path. That is consistent with the migration plan's temporary unregistered compatibility source.
- `ManagerExecutionLeaseStore.RemoveExpired` exists, but no expiry sweeper registration was found. Validation rejects expired leases, so this is cleanup and observability debt rather than a separate acceptance failure.

**Verdict: FAIL**
<promise>FAIL</promise>
