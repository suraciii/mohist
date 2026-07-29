# Review: Issue 522

## Findings

### High: Later Turns can remain Executing after an unconfirmed stop

`AgentSessionStopRoutes.ExecuteStopAsync` converts an unconfirmed reply into a durable Unknown state only for a launch Turn by calling `IAgentJobGrain.MarkUnknownAsync` (`packages/server/src/Mohist.Server/Api/AgentSessionStopRoutes.cs:129-133`). For a non-launch Turn it returns the Runner's `unknown` reply without changing the Session. That transition is instead delegated entirely to the Runner's best-effort outbox: `recordCancelActivity` returns immediately when the outbox is unavailable (`packages/runner/src/server/cancel-handler.ts:145-153`) but the handler still returns `{ state: "unknown" }` (`:126-138`).

Therefore, during durable snapshot recovery or an outbox failure, stopping a follow-up Turn with an unconfirmed Runtime result tells the caller `unknown` but leaves its persisted Turn `Executing` and Session activity `Active`. This violates the stop requirement that an unconfirmed stop leaves both target Turn and Session activity Unknown, and it can prevent the user from taking the intended recovery path. Make the Server synchronously record the target non-launch Turn/Session Unknown when it receives an `unknown` stop reply; retain the correlated Runner activity fact as idempotent convergence evidence. Add an API regression with no/unhealthy Runner outbox that asserts the persisted follow-up Turn and Session are Unknown.

## Verification

The current branch's recorded validation shows full .NET, Runner, and Web suites passing. This review found the missing later-Turn unconfirmed-stop state transition by tracing the Server route and Runner's intentionally best-effort activity fact path.

<promise>FAIL</promise>
