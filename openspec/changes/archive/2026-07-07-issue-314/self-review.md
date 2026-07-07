# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The spec's formal reducer-signature requirement contradicted the design decision. Spec said each reducer "SHALL have the signature `(prev, detail) => next`" (Requirement text and Scenario "Each directly-extractable event has a pure reducer"), but design D2 decides on `(prev, detail, env) => next` with `env = { now, isoNow, randomId }` so the reducers stay pure and deterministic (testing.md §2 forbids wall-clock / `Math.random` in unit-testable logic — needed by `plan_round_start`, `coder_recovery_status`, `session.liveness`, and the two compaction reducers). The design even acknowledged the discrepancy in its own "Alternative considered". An implementer following the spec literally would produce impure reducers that violate the same spec's "MUST NOT touch `Date.now()`" clause.
  Verification: Updated `specs/session-timeline-events/spec.md` Requirement text and the "Each directly-extractable event has a pure reducer" scenario to state the uniform `(prev, detail, env) => next` signature with `env: { now: number; isoNow: string; randomId: () => string }` injected by the hook. Spec now aligns with design D2 and with task T-003's acceptance criterion ("pure `(prev, detail, env) => next` functions where `env` carries `{ now, isoNow, randomId }`"). Re-read the spec section to confirm it is internally consistent (the "MUST NOT reference `Date.now()`" clause and the env-injection clause no longer conflict).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: consistency
  Evidence: The spec made a factually wrong claim about the widget barrel. Requirement text said "The widget barrel (`widgets/coder-session/index.ts`) SHALL continue to re-export the shared types/helpers ... the rest of the codebase imports today", and the scenario was titled "Widget barrel still re-exports shared types and helpers" with a WHEN clause importing "from `widgets/coder-session`". Verified against the codebase: `widgets/coder-session/index.ts` does NOT export `Round`/`RecoveryStatus`/`PlanProgress`/`ContextHealthState`/`deriveToolCallTitle`/`reconstructRoundsFromEvents` today — it exports only 8 unrelated session-card/composer symbols. Downstream consumers (`SessionTimeline.tsx`, cross-widget `PlanProgressPanel.tsx`) import these via the deep path `widgets/coder-session/model/useSessionTimeline`. The design (context line 9 + D7) and proposal (line 30) both correctly state the barrel is unchanged and re-exports flow through `useSessionTimeline.ts`. An implementer following the spec's old wording could try to ADD barrel exports to satisfy the scenario, contradicting design D7 and proposal line 30.
  Verification: Updated `specs/session-timeline-events/spec.md` — Requirement text now states the existing deep path `widgets/coder-session/model/useSessionTimeline` SHALL keep resolving via re-export from `useSessionTimeline.ts`, with the barrel unchanged; renamed the scenario to "Existing deep-path imports keep resolving after relocation" and rewrote its WHEN/THEN to reference the deep path and explicitly state the barrel remains unchanged. Spec now matches design D7, proposal line 30, and verified code reality (`index.ts` contents).
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: Task T-001 and design D6 stated `TaskStatusIcon` "remains exported" / is an "exported component". Verified in `SessionTimeline.tsx:339`: `function TaskStatusIcon(...)` has no `export` keyword — it is a local function used only by the local `TaskProgressPanel`. (The separately-existing `widgets/issue-workflow/ui/TaskProgressPanel` is a different component.) The decision to keep both is still correct; only the factual claim of export status was wrong.
  Verification: Updated `tasks.json` T-001 description and acceptance criterion, and `design.md` D6 (decision bullet + alternative-considered + Open Questions) to state `TaskProgressPanel` is the exported one and `TaskStatusIcon` is a local non-exported function. The "no contract change" intent is preserved; only the export-status wording now matches the code.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-002 is a standalone TEST task ("Add event-wiring integration tests before reducer extraction"). The review's feasibility guidance normally flags separate test tasks as too granular (tests belong inside implementation tasks). Here the separation is spec-mandated: the "Migration is test-first" Requirement requires these `dispatchAgentEvent`-driven integration tests to exist AND pass against the pre-extraction hook BEFORE T-003 begins, so they cannot be merged into T-003 (the extraction they guard) without violating the ordering constraint. T-003 already carries its own co-located reducer unit tests. Dependency chain T-001 → T-002 → T-003 is acyclic with strictly increasing priority.
  SuggestedAction: Keep T-002 as-is (spec-mandated exception). No change needed; recorded to show the granularity check was applied and the exception is justified.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: follow-up
  Evidence: After dead-state removal, `TaskProgressPanel` (coder-session) and `TaskStatusIcon` become unreferenced in-tree. The plan correctly defers deletion (Open Questions in design.md). There is also a cross-widget barrel-hygiene nit: `PlanProgressPanel.tsx` imports `type { PlanProgress }` via a deep path bypassing the `coder-session` barrel.
  SuggestedAction: Track a separate cleanup issue (already noted in design Open Questions) to delete the now-unused coder-session `TaskProgressPanel`/`TaskStatusIcon` and to normalize the `PlanProgressPanel` import through the widget barrel. Out of scope for issue-314.
  Status: follow-up

## Verification Summary

- **Alignment**: Every issue acceptance criterion (reducer extraction, flush-chain preservation, dead-state removal, return-shape/semantics/cadence/scoping invariance, test-first, green suites) traces to a spec Requirement + a task. No issue requirement missing or misinterpreted.
- **Completeness**: All five spec Requirements have tasks; all three tasks reference correct spec anchors; edge cases (self-review FAIL step extension, `timeout`→`failed` mapping, compaction placeholder round, attempt fallback order, unmounted-hook drop) are covered as scenarios.
- **Consistency**: After repairs, spec/design/tasks agree on reducer signature (`env`-injected), widget-barrel status (unchanged; deep-path re-export), and `TaskStatusIcon` export status. Design line-number references (D6) were verified accurate against the current code.
- **Feasibility**: Task granularity is appropriate (T-001 structural deletion, T-002 spec-mandated pre-extraction test net, T-003 extraction + co-located unit tests). No circular dependencies; each `dependsOn` points to an existing lower-priority task.
- **Dependency completeness**: T-001 `dependsOn: []`; T-002 `dependsOn: [T-001]`; T-003 `dependsOn: [T-002]`. All valid, acyclic.

<promise>PASS</promise>
