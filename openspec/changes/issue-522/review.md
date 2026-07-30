# Review: Issue 522

## Findings

### High: Terminal Runtime events do not recover an orphaned stop claim

The recovery clear exists only in `AgentSessionGrain.MarkTurnTerminalAsync` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1857-1867`). Production Runtime activity does not call that method: `AppendRuntimeEventsAsync` calls the static `ApplyRuntimeEventToDomain` (`:928`), whose terminal path directly invokes `session.MarkTurnTerminal` (`:1565-1590`). That transition leaves `PendingStop` intact.

After a Server/request loss, reactivate a Session with a claimed stop and deliver the normal correlated `session.activity` terminal fact. The Turn becomes terminal, but `PendingStop` remains persisted and `BeginFollowupAsync` continues throwing `StopOperationInProgressException` (`:428-429`) forever. The new recovery Spec calls `MarkTurnTerminalAsync` directly (`AgentSessionStopClaimRecoverySpecs.cs:48`), so it does not exercise the production event path.

Make claim settlement part of the shared terminal-fact lifecycle while preserving the active-request guard, and add a restart regression that delivers the terminal fact through `AppendRuntimeEventsAsync`.

### High: A retry can lose the in-flight guard during registry removal

`AgentSessionStopClaimRegistry.Unregister` removes an operation from the nested dictionary, observes it empty, then removes the outer dictionary entry (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionStopClaimRegistry.cs:21-29`). A same-Turn retry can call `Register` between that emptiness observation and `TryRemove`: it obtains the same nested dictionary and adds its operation, but the first request still removes the outer entry because its value matches. `IsActive` then reports false while the retry's Runner abort is still in flight.

A terminal fact or the first request's completion can consequently clear `PendingStop` and admit a later Turn while the retry still holds a session-scoped abort. This violates the D5/stale-stop guarantee that retries keep the claim until every old stop request settles. Use an atomic registration representation or a compare-and-retry removal that cannot detach a newly active registration, and add a deterministic interleaving regression.

### High: The local registry releases the guard for ambiguous or remote stop requests

The route clears its claim for every `InvokeAsync` exception or null reply in `AgentSessionStopRoutes.cs:135-169`. A SignalR caller can lose the reply after the Runner has accepted the command; `cancel-handler.ts` then continues toward the session-scoped `callCancel` (`packages/runner/src/server/cancel-handler.ts:102-141`) without a request fence. Clearing the claim lets a natural terminal fact admit a new Turn before that delayed abort reaches the Runtime, allowing the old request to interrupt new work.

The same registry is an in-memory singleton on the HTTP Server (`AgentSessionStopClaimRegistry.cs:5-29`), but the Session grain can execute on another Orleans silo. `MohistSiloRegistration` enables clustered silos (`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistSiloRegistration.cs:14-19`), so a terminal event on a different silo sees no registration and clears the durable claim while the route's local Runner request remains active.

Do not treat a lost reply as proof the command had no side effect. Persist/reconcile an unconfirmed dispatch or introduce a Runner command fence/correlation that survives node boundaries; use shared/durable ownership rather than a per-process dictionary. Add coverage for a delivered-but-unanswered stop and for a terminal fact processed on a different silo.

### Medium: Required later-Turn AgentJob-isolation scenarios are absent

T-002 and T-003 require Server tests proving that cancelling or stopping a follow-up Turn leaves an already-terminal AgentJob unchanged (`openspec/changes/issue-522/tasks.json:43,67`). `GenericAgentSessionCancelApiSpecs` uses jobless follow-up sessions, while `AgentJobCancellationSpecs` covers only a launch Turn (`packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Grain/AgentJobCancellationSpecs.cs:18-70`). No scenario creates a terminal Job, then controls a later Turn in that Session and asserts the Job verdict remains unchanged.

Add both integration-level regressions to lock the aggregate ownership boundary required by the issue.

## Verification

The required `mo issue show` command is not supported by the current CLI; `mo issue view 522` reports the issue as in-progress/check. The branch records a passing final `npm test`, but the findings above are uncovered lifecycle and concurrency paths.

<promise>FAIL</promise>
