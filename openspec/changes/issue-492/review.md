# Review Findings

## P1. Binding-reconcile batches can discard facts for the current binding

`packages/runner/src/server/runtime-event-outbox.ts:733-750` groups records by logical session, so a queued `binding-reconcile` record for an old physical `runtimeSessionId` can be batched with a later record for the same AgentSession after Reset or missing-session recovery has installed a new binding. `packages/runner/src/server/runtime-event-delivery.ts:73-79` then calls the runner-scoped runtime-events endpoint once, while `batchEnvelope` at `:97-105` puts `head.runtimeSessionId` in the request for every event in the batch. The server applies the binding guard once to that request (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:837-849`), so if the head is stale, the event for the current binding is discarded too; the outbox treats the successful HTTP response as acknowledgement and removes both records.

This can leave the current AgentSession activity stale after a reconnect: an old queued fact and a newer fact for the replacement binding are both sent under the old binding ID, neither settles the current binding, and neither is retried. Batch binding-reconcile records by physical binding ID as well as session, or deliver each binding generation separately, and add a regression test that queues old and new binding facts before delivery and verifies the new fact is applied.

<promise>FAIL</promise>
