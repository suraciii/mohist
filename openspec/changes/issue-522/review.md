# Review: Issue 522

## Findings

### High: Turn control's stale guard is not held through the side effect

`AgentSessionStopRoutes.ExecuteStopAsync` first reads `ResolveTurnControlAsync` (`packages/server/src/Mohist.Server/Api/AgentSessionStopRoutes.cs:64`) and later sends the session-scoped `CancelAgentSession` command (`:116-119`). Nothing revalidates that the same Turn is still executing between those two calls. The Runner receives the supplied `turnId`, but `cancel-handler.ts` only uses it when recording the resulting activity fact (`packages/runner/src/server/cancel-handler.ts:127-133`, `:145-177`); its actual `callCancel` at `:110` aborts the physical Runtime session.

Consequently, an executing target can finish after the grain read, its terminal fact can make the Session idle, and a later follow-up can begin on that same Runtime session before the delayed stop reaches the Runner. The old request then aborts the later Turn. This is exactly the stale-entry failure the Turn-addressing contract forbids, even though the later activity fact remains correlated to the original Turn. The cancel path has the same TOCTOU shape: it resolves a queued Turn (`AgentSessionCancelRoutes.cs:59`) and separately invokes the void `CancelTurnAsync` (`:92`); if a `session.input` fact promotes it in between, `CancelTurn` no-ops but the route still returns `state: "cancelled"` (`:93`).

Make target eligibility and the control transition/claim atomic in the Session grain, then ensure a stop cannot call the session-scoped Runtime abort once that claim is stale. The operation must return the settled target state rather than allowing the route to fabricate `cancelled`. Add deterministic API/Runner tests that interleave terminal-plus-next-Turn progression between the initial control request and Runner dispatch, and that interleave queued-to-executing before cancellation.

## Verification

The branch records passing full .NET, Web, and Runner validation. This review inspected the current control routes, Session-grain transitions, Runner handler, and API regressions; the existing stale tests cover a target already terminal before the request, not this read-to-side-effect interleaving.

<promise>FAIL</promise>
