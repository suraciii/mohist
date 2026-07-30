# Review: Issue 522

## Findings

### High: A persisted stop claim can permanently block a Session after Server loss

`ClaimTurnStop` writes `PendingStop` into `AgentSessionStatusSnapshot` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:697-704` and `AgentSession.cs:299`), so the in-flight HTTP stop is durable. `CompleteTurnStopAsync` clears it only from the still-running route's `finally` block. If the Server or the grain activation is lost after the claim is saved and before that callback, activation reloads the same `PendingStop` unchanged (`AgentSessionGrain.OnActivateAsync`, `:61-69`). There is no expiry, recovery protocol, or reconciliation path for it.

`BeginFollowupAsync` then unconditionally throws while `PendingStop` exists (`AgentSessionGrain.cs:425-429`), even if a later authoritative terminal Runtime fact has already marked the claimed Turn terminal. The Session can therefore remain permanently unable to accept another Turn after a crash or activation loss during a stop request.

Keep the stale-stop protection, but give the claim a durable completion/recovery lifecycle: for example, record enough correlation to reconcile it from the Runtime outcome, or expire/recover it safely after the request's owner is gone. Add a restart/activation regression that claims a stop, loses the request path before Runner reply, delivers the terminal fact, and proves a later follow-up is eventually admitted without allowing the old abort to affect it.

## Verification

The branch records passing .NET, Web, and Runner suites. This review traced the new persisted claim through activation and the follow-up gate; existing claim tests cover Runner replies in one live request but not ownership loss between claim and completion.

<promise>FAIL</promise>
