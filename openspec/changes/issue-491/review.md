# Review Findings

## P1: Session-close success can permanently strand the pending failure event

`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:995` delivers the terminal session close before appending `PendingFailureEvent`. On a successful close, `DeliverTerminalToSessionAsync` calls `ClearPendingAndReminderAsync` at `:1122`, which persists `PendingSessionClose = null` and unregisters the `agent-job-recovery` reminder at `:1135`. A process loss between that cleanup and `EmitFailureEventAsync` leaves the persisted `PendingFailureEvent` with no durable wake-up. On reactivation, `OnActivateAsync` only resumes a pending session close, so it does not restore the reminder or append this stranded failure event. The required activation-loss retry guarantee is therefore broken, and the event can be permanently absent from routing, inbox, and Hermes. Keep the reminder alive until both obligations are cleared, restore it for a pending failure event during activation, and add a recovery spec covering a successful session close followed by activation loss before event append.

<promise>FAIL</promise>
