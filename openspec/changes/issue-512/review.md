# Review: Issue 512

## Findings

### P1: Replays are rejected after the Agent or context changes instead of resuming the accepted launch

`packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:75-108` resolves the current Agent, rejects an archived Agent, validates context references, and resolves/validates the runtime override before it invokes the idempotent launcher at line 114. Therefore, a successfully accepted launch retried with its original `Idempotency-Key` after the Agent is renamed/archived or a referenced Issue/Epic changes is rejected by current validation, rather than reaching the coordinator and returning the original plan.

This violates design D1 and the `agent-launch-idempotency` scenario requiring an existing plan to be looked up before mutable validation. The current fingerprint also receives `agent.Id` as `AgentRef` in `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:156-164`, rather than the request path's supplied `agentRef`, so it cannot enforce the required conflict when a key is reused with a different Agent reference that resolves to the same Agent. Move existing-plan lookup/fingerprint comparison ahead of Agent/context/runtime resolution and retain the raw supplied Agent reference in the canonical request. Add specs for archive, rename, context mutation/removal, and distinct supplied references resolving to the same Agent.

### P1: Unknown-to-running reconciliation leaves the Session activity idle

`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:511-518` sets `AgentSessionActivity.Idle` for an `Unknown` initial turn. When authoritative Runner evidence later restores the Job from Unknown to Running, `AgentJobGrain.ReconcileRunningAsync` calls `MarkInitialTurnExecutingAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:351-359`), but `AgentSessionExtensions.MarkInitialTurnExecuting` only changes the turn status and `LastDataAt` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:444-468`); it never restores activity to `Active`.

Consequently, launch observation can report `turnStatus: executing` while `sessionActivity: idle`, and the Web session data source treats that Session as not running. This contradicts the observation contract that Session activity represents unfinished conversation work and the design requirement that activity follows the stored Turn fact. Make Unknown map to an unresolved Session activity as appropriate, and ensure the executing transition restores active activity. Cover Unknown -> running reconciliation in the composite observation and client state tests.

## Verification

- `npm test` passed.
- `npm run typecheck -w packages/runner && npm test -w packages/runner` passed.
- `npm run typecheck -w packages/web && npm run test:run -w packages/web && npm run check:fsd -w packages/web` passed.

<promise>FAIL</promise>
