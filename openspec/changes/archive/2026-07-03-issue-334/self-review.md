# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: consistency
  Evidence: The spec (requirement 2 / scenario "Persisted dirty run is corrected on next stop") and the proposal (What Changes bullet 2) both stated that already-persisted dirty runs (e.g. #331) are corrected "the next time its grain executes `Stop()` over that persisted state". This is impossible: `WorkflowRun.Stop()` guards against terminal status (`WorkflowRun.Lifecycle.cs:128` — `run.Status` not in the non-terminal set → throws `InvalidOperationException`), and design D2 itself explicitly notes "a run persisted as `Stopped` can never be re-stopped". So the spec contradicted both the code and its own design. An implementer following the spec would write a test that calls `Stop()` on a `Stopped` run, which throws and cannot pass.
  Verification: Cross-checked the `Stop()` guard in `WorkflowRun.Lifecycle.cs:126-132` (rejected set excludes `Stopped`) and design D2's own rationale. After repair, spec requirement 2 + scenario 3 and proposal bullet 2 say self-heal happens on grain reactivation (`OnActivateAsync` reconcile + write-back), matching design D2 and task acceptance #4 (which already said "grain activates").
  Status: resolved

- [ID: item-2]
  Severity: blocking
  Scope: feasibility
  Evidence: Design D2 and `tasks.json` specified the shared reconcile method's guard as `run.Status == Stopped && current.IsAwaitingApproval` AND called it from `Stop()` *before* the `Stopped` transition. At that call site `run.Status` is not yet `Stopped` (it is one of Pending/Ready/Running/AwaitingApproval/Paused), so the `Stopped` half of the guard is false and the cleanup becomes a no-op — the residual gate would remain dangling on every fresh stop, failing acceptance criterion #1 and spec scenario 1.
  Verification: Traced `Stop()` ordering (`WorkflowRun.Lifecycle.cs:126-132`) and D1's inline snippet (cleanup precedes `run.Status = Stopped`). The repair sets the method guard to the residual-gate condition `current.IsAwaitingApproval` (idempotent — false after first clear, so no write amplification), and moves the `Stopped` scoping to the `OnActivateAsync` caller so a live run genuinely awaiting approval is never disturbed. This lets one method serve both call sites and satisfies all four acceptance criteria.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: The proposal Impact section listed only `WorkflowRun.Lifecycle.cs` as touched, but D2 (accepted by the task `output`) also touches `Shared.cs` (new `ReconcileStoppedApprovalGate`) and `WorkflowGrain.cs` (`OnActivateAsync` reconcile + write-back). The task `output` field already enumerates all three; the proposal was incomplete.
  Verification: Confirmed `ReconcileReadyStatusWithInFlightWork` lives in `Shared.cs:55` and the grain rehydration write-back pattern is at `WorkflowGrain.cs:67-74`. Added both files to the proposal Impact so proposal and task agree on blast radius.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: completeness
  Evidence: After the item-1 repair, spec scenario 3 additionally asserts "the cleaned state SHALL be written back to the run store" — a grain-level (`OnActivateAsync` → `SaveAsync`) behavior. The task's test plan enumerates a domain-method unit spec (`ReconcileStoppedApprovalGate_CorrectsPersistedDirtyRun`) mirroring `ReconcileReadyStatusWithInFlightWork`, but does not explicitly call out a grain-level write-back test.
  SuggestedAction: During implementation, confirm whether the existing grain reactivation/write-back path already has spec coverage (as for `ReconcileReadyStatusWithInFlightWork`); if not, add a minimal grain-level assertion that a rehydrated `Stopped`+dirty run is corrected and saved on activation.
  Status: follow-up

<promise>PASS</promise>
