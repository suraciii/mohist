## Context

Mohist workflow runs advance through an ordered list of stages (`plan → build → check → integrate …`). Today the control plane offers only two recovery shapes:

- `retry` (`WorkflowRun.Failure.cs:28`) — re-enqueues the single failed task/check **of the current stage**, in place (no new attempt).
- `rerun` (`WorkflowRun.Failure.cs:121`) — rebuilds the **current stage** as a new `StageRun` (`Attempt + 1`, `Initialized = false`), emits `StageStarted`, and lets `CommitAsync → InitializeFreshStagesAsync` (`WorkflowGrain.cs:767`) lazily reload tasks/checks.

There is no way to invalidate a *range* of stages and resume from an earlier, already-reached stage. Users who need to re-drive `build` (or later) after a template change or a missing runtime variable must either rerun the current stage only, retry one task, or abandon the run. See `proposal.md` for motivation and `specs/workflow-stage-rerun/spec.md` for formal requirements.

Relevant current-state facts gathered from the code (these constrain the design):

- `WorkflowRun.Stages` is a mutable `List<StageRun>` created up-front in `Create()` (`WorkflowRun.Lifecycle.cs:47`); every stage starts `Attempt = 1`, `Initialized = false`, `Status = Pending`. Stages get `Initialized = true` only when `StageStarted` is processed by `InitializeStage` (`WorkflowRun.Stage.cs:11`).
- `StageRun.Attempt` is `init`-only (`StageRun.cs:12`), so "bumping the attempt" always means constructing a brand-new `StageRun` and replacing it by index — exactly what `Rerun()` does.
- `CurrentStageId` is advanced **only** inside `Advance()` (`WorkflowRun.Stage.cs:38`) when a stage completes. `Rerun()` never touches it (it re-runs the current stage). Resuming from an *earlier* stage therefore requires explicitly setting `run.CurrentStageId = target`.
- Stages strictly after `CurrentStageId` are always fresh/uninitialized (the run has not reached them). So "the furthest stage reached" == the current stage, today.
- Domain validation everywhere throws plain `InvalidOperationException` (`WorkflowRun.Failure.cs:31,35,57`); the `/rerun` route does **not** catch it (it only sniff-detects deserialize corruption, `IssueRoutes.WorkflowControl.cs:200`). The one typed exception, `WorkflowSessionContextExhaustedException`, is caught in `/retry` and mapped to a structured 409 (`IssueRoutes.WorkflowControl.cs:84`). So actionable errors need explicit handling — they do not surface as anything other than 500 today.
- Stage locks are released via `ReleaseStageLocksAsync(stage, reason)` (`WorkflowGrain.cs:534`), where `reason` is a free-form string (`"retried"`, `"rerun"`). `ReleaseCurrentStageLocksAsync` only covers `_run.CurrentStageId`; release is idempotent (releasing an unowned/non-sequential stage is a no-op via `GetSequentialLockResourceAsync` returning null, `WorkflowGrain.cs:566`).
- `TaskRunStatus { Pending, Running, Completed, Failed }` (terminal = Completed/Failed); `StageCheckStatus { Pending, Running, Passed, Failed }`. There is no shared "is there active work" helper — the building blocks (`RunningTask`, `HasNoPendingTasksAndPassedChecks`, `WorkflowRun.Stage.cs:79,110`) exist but are stage-local.
- The CLI registers `retry`/`rerun` through a body-less generic builder `BuildAction` (`MohistCliCommands.Issue.cs:559`); a command that must send a JSON body uses a dedicated builder (e.g. `BuildReject:587`).

Stakeholders: workflow control-plane (server domain + grain), HTTP API, `mo` CLI. Web UI is explicitly out of scope (Non-Goals).

## Goals / Non-Goals

**Goals:**
- Deliver a pure control-plane range-invalidation action `rerun-from-stage` that resumes a run from any stage it has already reached.
- Preserve all execution facts: runtime variables, workspace/git, external side effects, and the results of stages before the target.
- Reject unsafe invocations with actionable, machine-readable errors (unknown stage, never-reached stage, active work in range) and leave run state untouched on rejection.
- Keep stage locks consistent (no residual/orphan locks in the range) and reuse the existing lazy stage-(re)initialization path for later stages.
- Match the existing `retry`/`rerun` patterns end-to-end (domain method → grain method → HTTP route → CLI subcommand) without altering their semantics.

**Non-Goals:**
- No run-level template freeze; later stages load the currently-effective template when advance reaches them.
- No clearing of runtime variables; no retention of invalidated `StageRun` history; no timeline history of old attempts.
- No rollback of workspace/git/external side effects; no enforcing of action reentrancy (that is an action contract).
- No Web UI; no merging of `retry`/`rerun`/`rerun-from-stage` into one command.

## Decisions

### D1. One validate-and-mutate domain method `RerunFromStage(stageId)`

Add `RerunFromStage(string stageId)` to the `extension(WorkflowRun run)` block (alongside `Rerun()`, in `WorkflowRun.Failure.cs`). It validates **and** mutates in a single call, returning `IReadOnlyList<WorkflowEvent>` — the same shape as `Rerun()`. This keeps all invariants inside the aggregate, consistent with how `Retry()`/`Rerun()` already combine guard + mutation.

Pseudocode of the contract:

```
targetIdx = Stages.FindIndex(s => s.Id == stageId)
currentIdx = Stages.FindIndex(s => s.Id == CurrentStageId)
if targetIdx < 0                       -> reject "unknown_stage"        (actionable)
if targetIdx > currentIdx              -> reject "stage_not_reached"    (actionable, list eligible)
if any stage in [targetIdx..end] has a non-terminal task or a Pending/Running check
                                       -> reject "active_work_in_range" (actionable)

Stages[targetIdx]     = new StageRun { Id=same, Attempt=old+1, RequiresApproval=old, Status=Running }   // Initialized=false
Stages[targetIdx+1..] = new StageRun { Id=same, Attempt=1,     RequiresApproval=old, Status=Pending }   // Initialized=false (fresh)
CurrentStageId = Stages[targetIdx].Id
Failure = null
Status  = Running
return [ WorkflowRunResumed, StageStarted(Stages[targetIdx].Id) ]
```

- Later stages are **not** initialized here. The emitted `StageStarted(target)` drives the existing `CommitAsync → InitializeFreshStagesAsync → InitializeStage → Advance` loop (`WorkflowGrain.cs:767`, `WorkflowRun.Stage.cs:11`) to (re)load each subsequent stage from the current template when advance reaches it — satisfying the "later stages reinitialize against the current template" scenario.
- `RequiresApproval` is copied from the prior `StageRun` (as `Rerun()` does) so approval semantics survive invalidation.
- Later stages are reset to `Attempt = 1`, `Status = Pending` (matching a brand-new stage from `Create()`), distinguishing them from the target's `Attempt + 1`.

**Alternative considered:** split into `ValidateRerunFromStage` + `ApplyRerunFromStage` so the grain can release locks strictly between validation and mutation. Rejected — the grain is single-threaded (turn-based Orleans), and lock release after a successful mutation but before `CommitAsync` (which is what persists state) is equally safe and avoids a two-method surface. The single-method form also matches `Rerun()`.

### D2. "Reached stage" = position-based, no new persisted state

A stage counts as reached iff `targetIdx <= currentIdx`. This needs **no** new field on `WorkflowRun`: the current stage is, by construction of the forward-only advance + the existing `Rerun()`, always the furthest point the run has reached. Stages after it are pristine.

**Alternative considered:** track an "ever-reached high-water mark" so that after a `rerun-from-stage(plan)` the run could still jump back to a later stage (e.g. `integrate`) that it had reached earlier in its lifetime. Rejected because:
1. It diverges from the practical UX — a user who just chose to rerun from `plan` wants `plan→…→integrate` to re-execute; jumping to `integrate` immediately is not a real intent.
2. It requires a new persisted field on `WorkflowRun` plus reasoning about reset semantics, against the "data model as simple as possible" guideline.
3. The spec's parenthetical ("equivalently, its position is not strictly ahead of the furthest stage the run has progressed into") is satisfied by the position-based check.

The `stage_not_reached` / `unknown_stage` error payload includes `eligibleStages` (the ids with `idx <= currentIdx`) so the client can render a picker — required by the spec.

### D3. Active-work detection lives in the domain; rejection leaves state untouched

The active-work scan is part of D1's validation, reading only `_run.Stages` (domain state), so it belongs in the aggregate. A task is "active" when `Status is not (Completed or Failed)` (i.e. `Pending` or `Running`); a check is active when `Status is (Pending or Running)`. The scan covers **every** stage in `[targetIdx..end]`, not just the target — per the spec's "Active task in a later stage blocks" scenario.

Because validation precedes any mutation in D1, a rejection throws before `CurrentStageId`, `Stages`, `Failure`, or `Status` are touched, satisfying "the run's stage list, `CurrentStageId`, `Failure`, and `Status` SHALL remain unchanged". The grain performs **no** lock release and **no** `CommitAsync` on this path, so there is also no external side effect on rejection.

Orleans grains are single-threaded per activation, so the in-memory scan is consistent with the grain's own task-completion calls (`CompleteTaskAsync`, check-result handlers) — no concurrent mutation race within the grain.

### D4. Actionable errors via a typed exception mapped at the route

Introduce a small typed exception (mirroring the `WorkflowSessionContextExhaustedException` precedent) — e.g. `WorkflowControlRejectionException(Code, Message, Details)` — thrown by `RerunFromStage` for the three actionable cases. It propagates untouched through the grain (grain methods stay `Task`-returning, like `RetryAsync`/`RerunAsync`) and is caught **only** in the new route, which maps:

| Domain code            | HTTP | `ApiResults` helper |
|------------------------|------|---------------------|
| `unknown_stage`        | 400  | `BadRequest`        |
| `stage_not_reached`    | 400  | `BadRequest` (details: `eligibleStages`) |
| `active_work_in_range` | 409  | `Conflict`          |

Body/empty-stage validation (`stage` missing/whitespace) → `BadRequest` 400 at the route, before the grain call. No-workflow-run → 404 via the existing `ResolveWorkflowControlAsync`.

**Alternatives considered:**
- *Throw plain `InvalidOperationException` and message-sniff in the route* — rejected; the `/rerun` deserialize-corruption sniff (`IssueRoutes.WorkflowControl.cs:200`) already shows this is fragile and we need machine-readable codes.
- *Change the grain to return a `Result<Success, Error>`* — rejected; it breaks the uniform `Task`-returning grain-control surface and is a larger change for one action. The typed-exception path is already established for `WorkflowSessionContextExhaustedException`.

### D5. Stage-lock release iterates the range; success path only

In the new grain method `RerunFromStageAsync(stageId)`, **after** `_run.RerunFromStage(stageId)` succeeds and **before** `CommitAsync`, iterate `Stages[targetIdx..end]` and call `ReleaseStageLocksAsync(stage.Id, "rerun-from-stage")` for each. Releasing is idempotent (`GetSequentialLockResourceAsync` returns null for non-sequential stages, and `IWorkflowStageLockGrain.ReleaseAsync` ignores unowned locks), so iterating the whole range is safe and matches the spec's "any sequential stage lock … within the target-to-end range". Locks for stages **before** the target are never touched (they are simply never iterated).

Ordering rationale: validation (D1) throws first, so no lock is released on rejection. Releasing after mutation but before persisting is safe because lock state lives in a separate `IWorkflowStageLockGrain` and the run's mutated state is not durable until `CommitAsync → SaveRunAsync`; a failure between the two leaves the prior persisted run authoritative on the next activation reload.

**Alternative considered:** release only the *old* current stage's lock (the only one realistically held). Rejected — the spec explicitly scopes release to the whole range, and the iterative form is correct for workflows where more than one sequential resource exists, at negligible cost.

### D6. HTTP route mirrors `/rerun`; CLI gets a dedicated builder

- **Route** (`IssueRoutes.WorkflowControl.cs`): `POST /{number:int}/rerun-from-stage` inside `MapIssueWorkflowControl`. Request DTO `internal sealed record RerunFromStageRequest(string? Stage)` (same style as `RejectRequest`). Flow: `GetRequiredProject` → `ResolveWorkflowControlAsync(..., WorkflowControlAction.RetryOrRerun)` (reuses the existing `failed`-eligible gate) → body validation (non-empty `stage`) → `grains.GetGrain<IWorkflowGrain>(wrId).RerunFromStageAsync(stage)` → catch `WorkflowControlRejectionException` and map per D4; on success `ApiResults.Ok()`. Also keep the `IsWorkflowRunStateCorruption` self-heal catch that `/rerun` uses, for parity.
- **CLI** (`MohistCliCommands.Issue.cs`): a dedicated `BuildRerunFromStage` (not the body-less `BuildAction`) carrying a required `--stage <id>` option, modeled on `BuildReject`. It resolves the project (`api.ResolveProjectIdAsync`), then `api.PrintPostAsync(ProjectIssuesPath(projectId, $"/issues/{number}/rerun-from-stage"), new { stage })`. Missing `--stage` reports a usage error and makes no request. Server-provided code+message surface through the existing `PrintResponseAsync` path.
- **Grain interface** (`IWorkflowGrain.cs`): add `Task RerunFromStageAsync(string stageId);` next to `RerunAsync`.

### D7. Grain method ordering (concrete)

```
RerunFromStageAsync(stageId):
    EnsureRun()
    var events = _run.RerunFromStage(stageId)          // D1: validate+mutate, throws D4 on rejection
    var targetIdx = _run.Stages.FindIndex(s => s.Id == stageId)
    for i in [targetIdx .. _run.Stages.Count-1]:
        await ReleaseStageLocksAsync(_run.Stages[i].Id, "rerun-from-stage")   // D5
    log "rerun-from-stage at stage={stageId}"
    await CommitAsync(events)                           // InitializeFreshStagesAsync loads target tasks
```

This is `RerunAsync` generalized: same skeleton (`EnsureRun` → release locks → domain mutation → `CommitAsync`), with the mutation first so validation can abort before any side effect.

## Risks / Trade-offs

- **[Position-based "reached" loses lifetime history after a backward jump]** → After `rerun-from-stage(plan)`, a previously-reached later stage is no longer selectable until the run advances back to it. Mitigation: this matches user intent (re-driving the range) and the `eligibleStages` payload makes the constraint discoverable. Documented as a deliberate Non-Goal (no high-water mark).
- **[Action reentrancy not enforced by the engine]** → Re-executed actions run on a workspace containing their own prior artifacts. Mitigation: this is an explicit action contract (spec: "Reached actions must be reentrant"); `mohist/github-pr`'s `open-draft-pr`/`merge-github-pr`/`push` already satisfy it. Non-reentrant actions are the action owner's responsibility, not the engine's.
- **[Runtime variables preserved across attempts]** → A new attempt's `setVars` silently overwrites same-named keys; stale keys from a prior attempt that the new attempt does not rewrite remain visible. Mitigation: this is mandated by the spec ("variables are run-scoped execution facts"); overwrite semantics are the documented contract.
- **[No rollback of external side effects]** → A re-executed `merge-github-pr` will not un-merge. Mitigation: by design (Non-Goal); operators rely on action reentrancy/idempotency, and integration stages that are unsafe to re-run should gate on existing artifacts.
- **[Lock release + mutation span two grain calls before persist]** → A crash between `ReleaseStageLocksAsync` and `CommitAsync` could release a lock without the run reflecting the resume. Mitigation: the run reloads from persisted state on reactivation (the un-persisted resume is discarded), and lock release without a resumed run is a benign "free" outcome that lets waiters proceed; worst case is the user retries the action. No orphan lock is created because release only ever frees locks this run owned.
- **[D4 introduces a new exception type to the control surface]** → Slight surface growth. Mitigation: it is thrown only by `RerunFromStage` and caught only by the new route; it generalizes cleanly if future control actions need actionable rejections.

## Migration Plan

No persistence migration, no external-side-effect rollback, no data format change — `StageRun`/`WorkflowRun` shapes are unchanged (new `StageRun`s built here are structurally identical to those `Create()`/`Rerun()` already produce).

- **Deploy:** ship server (domain + grain + route) and CLI together. Existing `retry`/`rerun` paths are untouched; the new endpoint and command are purely additive.
- **Rollback:** revert the code change. Runs created while the feature was live remain valid (their `StageRun`s are standard); only the `rerun-from-stage` action becomes unavailable. No data repair needed.
- **Activation:** Orleans grains rehydrate from existing storage; no grain-state version bump is introduced.

## Open Questions

- **Advertising eligible stages proactively.** The rejection payload lists `eligibleStages`, but the "available actions" projection (`WorkflowStatusMapper.BuildAvailableActions`) currently advertises only `retry`/`rerun` for failed runs. Should this issue also add a `rerun-from-stage` entry (per reached stage) to that view, or is the CLI/API-only surface sufficient until the Web UI follow-up? Lean: skip the projection change here to stay in scope; the error payload is enough for CLI users.
- **Should `rerun` eventually delegate to `rerun-from-stage(currentStage)`?** Explicitly deferred per Non-Goals (keep the three actions distinct this issue); revisit as a separate UX decision.
