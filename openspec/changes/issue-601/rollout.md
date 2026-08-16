# Workflow Boundary v1 Rollout

The Workflow completion boundary is a single fail-closed transition. There is no compatibility mode and no rollback to a server that ignores enriched reports.

1. **Quiesce and drain.** Stop new Workflow dispatch, let active pre-v1 runners finish or stop, and retain every started or completed local journal fence.
2. **Upgrade runners.** Register only runners that advertise `workflow-task-completion-boundary-v1`. Generic Actions, `mohist/pi`, and `mohist/opencode` all use the outer completion-boundary executor and persist the boundary before report delivery.
3. **Enable admission.** Reopen dispatch only after v1 runners are registered. The server rejects Workflow claims from runners without the capability. Plain task reports without a completion boundary are fail-closed and cannot use the old settlement path.
4. **Reconcile legacy fences.** An upgraded runner never reruns a pre-v1 started fence and never replays a pre-v1 plain result. When the dispatch identity validates, it reports a deterministic non-settling `unconfirmed` observation with reason `boundary-missing`; otherwise it retains the local fence for operator reconciliation. Local records remain until an accepted idempotent acknowledgement.
5. **Operate and monitor.** Monitor rejected claims/reports, boundary-missing recoveries, acknowledgement retries, duplicate reports, and recovery age. Reclaim or adopt work only through the generation-aware recovery operations.

Rollback is permitted only after Workflow dispatch is quiesced and all affected fences have been reconciled under v1. The rollback procedure must not depend on a server that understands only legacy `WorkResult` settlement; the v1 fail-closed report boundary remains authoritative until reconciliation is complete.
