## Context

Check review repair is currently represented indirectly through `fix-review-findings` task rows, `review-passed` check rows, retry/rerun endpoints, review artifacts, and hard-coded repair policy values. WorkflowRun already owns the canonical stage/task/check state for new runs and `GET /api/issues/:number/stage-state` already projects that state for the UI, but it does not expose a user-facing Check repair summary.

The main ambiguity is semantic rather than execution-only: a completed repair task means the repair agent finished an attempt, while the follow-up `review-passed` check remains the authoritative verdict. The implementation should therefore pull this relationship into one explicit projection instead of asking every client to infer it from task and check lists.

## Goals / Non-Goals

**Goals:**

- Expose a structured Check review repair state through stage-state for Check runs.
- Keep Check repair budget and scheduling policy authoritative in one place.
- Preserve `blockedReason` as a concise failure label while moving detailed repair explanation into structured data.
- Let Issue Detail render repair status, repair budget, follow-up review result, stop reason, unresolved review summary, and clear next actions.
- Keep repair task completion visually distinct from review gate success.

**Non-Goals:**

- Redesign the full issue detail page or pipeline timeline.
- Change review quality thresholds or convert failed reviews into passing reviews.
- Add unlimited or hidden repair loops.
- Collapse retry checkpoint, rerun review, and repair findings into one ambiguous action.

## Decisions

### D1: WorkflowRun check failure policies are the authoritative repair budget

Use the Check stage definition in `packages/cli/src/workflow/domain/index.ts` as the source of truth for `review-passed` repair policy. Remove or delegate the legacy `CheckStageRunner.getCheckFailurePolicies()` values so it cannot expose a different `maxAttempts` for the same check.

The repair projection and scheduler should both read the same policy data: `checkName = review-passed`, `fixTaskId = fix-review-findings`, and `maxAttempts`. Existing compatibility code may still translate old task ids such as `repair-review-findings` when reading historical rows, but new scheduling and user-visible budgets should use the WorkflowRun policy.

**Alternatives considered:** Keep the legacy runner policy and teach the UI which one wins. This preserves the conflict that caused the product bug and leaks implementation history into the UI. Add a separate config constant. This reduces duplication only if both WorkflowRun and runner consume it, but WorkflowRun already has the stage definition boundary and should remain the deeper module.

### D2: Add a computed `checkRepair` projection to Check `StageStateRead`

Extend `StageStateRead` with an optional Check-only repair field, for example:

```ts
interface CheckRepairState {
  checkName: 'review-passed'
  fixTaskId: 'fix-review-findings'
  status: 'not-needed' | 'available' | 'pending' | 'running' | 'completed' | 'exhausted'
  attemptsUsed: number
  attemptsMax: number
  attemptsRemaining: number
  repairAvailable: boolean
  lastRepairTask: StageTaskState | null
  lastRepairStatus: StageTaskStatus | null
  followUpReviewStatus: StageCheckStatus | null
  stopReason: 'review-passed' | 'repair-pending' | 'repair-running' | 'max-repair-attempts-reached' | 'manual-rerun-required' | null
  unresolvedSummary: string | null
}
```

Compute this in `StageStateService` when projecting Check state from WorkflowRun, and use the same helper for legacy fallback where enough task/check rows exist. The helper should derive attempts from tasks whose ids are `fix-review-findings` or start with `fix-review-findings:`. It should derive the follow-up review from the current `review-passed` check state, not from the repair task output. It should extract unresolved summary from the latest failed `review-passed.output` or `message` using tolerant parsing, because review outputs may differ across historical runs.

Do not persist a new repair-state table. The persisted facts remain tasks, checks, and WorkflowRun policy; `checkRepair` is a read model for clients.

**Alternatives considered:** Compute repair state only in the Web UI. This repeats workflow policy and review-output parsing in the client, increases the chance of inconsistent CLI/API behavior, and makes tests harder to anchor. Persist repair state as a new aggregate field. This adds migration and synchronization complexity without a new fact source; the state is fully derivable from existing runtime facts plus policy.

### D3: Keep repair attempt outcome separate from review verdict

Render repair tasks in the existing task list as attempts, but make the Check repair panel state the semantic bridge: `Last repair completed, follow-up review failed` when the last `fix-review-findings` task is completed and `review-passed` is failed. The UI must not infer review success from task status or wording such as `Fix review findings completed`.

Backend projection should preserve the task as `completed`, preserve the check as `failed`, and expose both facts together through `checkRepair`. The frontend should use labels such as `Repair task completed` for the task and `Follow-up review failed` for the gate result.

**Alternatives considered:** Rename or downgrade completed repair tasks to `failed` when the follow-up review fails. That would corrupt task semantics: the repair agent completed its assigned attempt even if the attempt was insufficient. Hide repair tasks from the task list and show only the panel. That removes useful audit history and conflicts with the existing runtime-added task visibility contract.

### D4: Separate recovery intents in API and UI

Keep existing `POST /api/issues/:number/retry` as checkpoint retry and label it `Retry checkpoint`. Keep `POST /api/issues/:number/rerun` as stage rerun; for Check review failures label the user intent as `Rerun review only` when the implementation only invalidates/re-runs Check review work rather than promising another repair.

Add a dedicated repair action path for Check review findings, either as a narrow endpoint such as `POST /api/issues/:number/check/repair-review-findings` or as an existing WorkflowRun application-service action that appends/reuses `fix-review-findings`. The endpoint should be idempotent for pending/running repair work and should reject or explain when automatic repair is exhausted unless the request is explicitly allowed as a manual user-requested repair. The UI should show `Fix review findings` only when `checkRepair.repairAvailable` is true or when manual repair is allowed by the endpoint contract.

Retrying a failed Check after repair exhaustion should not append another `fix-review-findings` task. It may reset `ai-review` and checks for checkpoint recovery, but the returned state and UI copy must continue to show the repair budget as exhausted.

**Alternatives considered:** Reuse `Retry` for repair. This is the current ambiguity and should be removed. Make `Rerun Stage` always rerun repair. This would blur review verification and code-modifying repair, and could create unintended extra code changes.

### D5: Keep the UI change local to Check failure surfaces

Add a compact Check repair panel near the blocked action area and/or Check stage progress on Issue Detail. The panel reads `stageState.stages.find(stage === 'check')?.checkRepair` and renders:

- Failure summary from `review-passed` or `checkRepair.unresolvedSummary`.
- Auto-fix status, attempts used/max/remaining, last repair task status, follow-up review status, and stop reason.
- Action buttons labeled by intent: `Fix review findings`, `Rerun review only`, and `Retry checkpoint`.
- Manual guidance such as `Take over manually` when repair is exhausted.

Existing task and check lists should remain, but copy should avoid presenting `fix-review-findings` completion as review pass. Query invalidation should refresh `issues`, `stage-state`, `workflow-run`, and `agent-status` after repair/retry/rerun actions.

**Alternatives considered:** Replace the whole pipeline view with a Check-specific workflow. That is larger than the problem and conflicts with the non-goal to avoid redesigning Issue Detail. Show only tooltips on existing buttons. Tooltips do not provide the structured state users need after repeated failures.

## Risks / Trade-offs

- [Risk] Historical legacy runs may have old task ids or incomplete check output. → Mitigation: support known aliases for reading, use tolerant output parsing, and show `unknown`/fallback messages instead of hiding the panel.
- [Risk] Adding a repair action endpoint could bypass WorkflowRun ordering rules. → Mitigation: implement it through the WorkflowRun application service and append/reuse repair tasks only when the current stage is Check and the failed check is `review-passed`.
- [Risk] Users may overuse manual repair after budget exhaustion. → Mitigation: default automatic repair remains bounded; UI copy must distinguish automatic budget from any explicit manual override.
- [Risk] Stage-state response grows with a Check-specific field. → Mitigation: keep it optional and only populated for Check stages, preserving existing task/check fields for other stages.
- [Risk] Review output formats vary. → Mitigation: extract unresolved summary opportunistically from known fields and fall back to check message or a generic failed-review message.

## Migration Plan

1. Add shared types for `CheckRepairState` in backend stage-state service and frontend API types.
2. Refactor Check repair policy so WorkflowRun stage definition is authoritative and legacy runner values cannot diverge.
3. Add `StageStateService` helper to compute `checkRepair` from Check tasks, checks, failure details, and policy.
4. Expose the field through `GET /api/issues/:number/stage-state`; no database migration is required because the field is computed.
5. Add or wire a dedicated repair-findings action through WorkflowRun application service and API, preserving idempotency for in-flight repair tasks.
6. Update Issue Detail and relevant progress components to render the Check repair panel and intent-specific actions.
7. Add backend and frontend regression tests for repair exhaustion, checkpoint retry without new repair scheduling, and repair-completed plus follow-up-review-failed display.

Rollback is straightforward: the computed `checkRepair` field is additive, so older clients can ignore it. If the repair action endpoint causes issues, hide the UI action and keep existing retry/rerun behavior while preserving the read-only repair explanation.

## Open Questions

- Should manual `Fix review findings` be allowed after automatic repair budget is exhausted, or should exhaustion require manual code takeover plus `Rerun review only`? The endpoint should make this explicit before implementation.
- Should `Rerun review only` reuse the existing `/rerun` endpoint with Check-specific behavior, or should it get a narrower endpoint that invalidates only `ai-review` and review checks?
