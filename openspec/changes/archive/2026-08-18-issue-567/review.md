# Issue 567 Review

## Verdict

PASS — no must-fix problems remain on the current change; it is ready to merge.

## Re-review Disposition Checks

- **Prior MF-1 (missing AgentSession state discarded the visibility delivery): fixed properly.** Both owner grains now retain the durable delivery head when `session.GetAsync()` returns null. Workflow delivery does this in `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:774-795`; AgentJob delivery does it in `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:602-633`. The queue is removed only after `ApplyInterruptionAsync` succeeds, and the Workflow interruption reminder or AgentJob recovery reminder remains available while the queue is non-empty (`WorkflowGrain.AgentResultSettlement.cs:23-27`, `AgentJobGrain.cs:1869-1887`).
- **The repair covers both affected owner types.** `RunnerUpdateInterruptSpecs.UpdateInterrupt_MissingSessionRetainsVisibilityDeliveryUntilSessionMaterializes` verifies both `interrupting` and `interrupted` Workflow deliveries are retained and replayed after materialization (`packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerUpdateInterruptSpecs.cs:67-115`). `AgentJobGrainSpecs.MissingSessionRetainsVisibilityDeliveryUntilSessionMaterializes` verifies the equivalent AgentJob path (`packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Grain/AgentJobGrainSpecs.cs:239-283`).
- **Earlier receipt-replay and handoff findings remain fixed.** The current tree still retains exact receipt replay, operation-ledger repair, bounded shutdown handoff, replacement fencing, and actionable stop-failure behavior. The current full Server SpecTests run covers the affected paths without regression.

## Dimension Checks

- **Acceptance coverage:** checked, no issue. The durable update fence, prompt bounded shutdown, runtime-confirmed receipt protocol, reconnect replacement dispatch, duplicate-delivery fencing, interruption visibility, and bounded per-work CLI reporting are implemented and covered by the issue-scoped tests.
- **Correctness:** checked, no issue. A missing or temporarily unavailable Session no longer turns a committed owner transition into a silently successful visibility delivery; the owner keeps retrying until the Session projection accepts the idempotent transition.
- **Regression check:** checked, no issue. The missing-session change only changes queue retirement and reminder retry behavior; it does not alter owner fencing, receipt arbitration, recovery generations, dispatch identities, or terminal-state handling.
- **Consistency:** checked, no issue. The Workflow and AgentJob implementations now use the same durable owner-owned delivery pattern and preserve the existing idempotent Session projection contract.
- **Tests:** checked, no issue. Focused Workflow/AgentJob regression classes pass 27/27. The full `npm run verify` gate passes: build with 0 warnings/errors; Workflow 178, CLI 1,856, Server Unit 2,674, Server Spec 3,950, Server Arch 68, Web 4,727, Runner 1,662, and Slack 70.

## Observations

1. The compatibility branch for older persisted interruption records without a `PendingSessionInterruptionDeliveries` entry still attempts a direct Session repair rather than reconstructing a queue when that Session is absent (`WorkflowGrain.Reports.cs:804-818` and `AgentJobGrain.Recovery.cs:616-633`). Newly created update fences queue before owner persistence and are covered by the fix; a migration/backfill strategy would make pre-fix records equally robust.
2. The CLI continues to map a `receipt-acked` recovery phase to a user-facing recovered result before replacement execution necessarily settles. The underlying phase remains persisted and visible, and the plan treats receipt acknowledgement as the bounded update reporting boundary.
3. Historical `RunnerUpdateOperation` records remain retained without a visible compaction policy. This is a maintenance concern outside issue 567's acceptance criteria.

<promise>PASS</promise>
