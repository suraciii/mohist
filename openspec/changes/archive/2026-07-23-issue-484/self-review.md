# Self-Review — Issue 484 (second pass)

Reviewed all artifacts under `openspec/changes/issue-484/` (`proposal.md`, `design.md`,
`tasks.json`, `specs/`) against the issue body and the current codebase. This pass verifies
that the four findings from the first review are resolved and checks for newly introduced
issues.

## Previous findings — resolution check

### F1 (Significant) — OpenCode Reset: RESOLVED

The three-way inconsistency (spec required it, design left it open, no task covered it) is
fixed:

- **Design**: "Open Questions" renamed to "Resolved Questions" with the decision: "OpenCode
  Reset is included in this change… T-003 wires the OpenCode reset command through
  `OpenCodeRuntime.createSession` + the unified rebind (D4)." The risk note is updated from
  "not implemented" to "this change wires it."
- **Tasks**: T-003 title, description, an acceptance criterion ("An idle OpenCode session
  receiving Reset creates a new empty Runtime Session via `OpenCodeRuntime.createSession`
  and binds it through the unified rebind; the runner no longer returns unavailable for
  OpenCode reset"), output, and notes all cover the OpenCode `SessionCommand` reset wiring.
- **Spec**: the unconditional "Reset SHALL" requirement and "A reset is requested while idle"
  scenario are intact; they now have task coverage.

Spec, design, and tasks are aligned.

### F2 (Moderate) — ContextExhaustionClassifier: RESOLVED

T-001 description now states: "Re-wire `ContextExhaustionClassifier`… to classify turn-failed
runtime events instead… if the turn-failed diagnostics are insufficient to classify,
explicitly remove the classifier." An acceptance criterion verifies the classifier no longer
depends on `session.closed` and is either re-wired or explicitly removed with documented
fallback. The classifier's fate is no longer unspecified.

### F3 (Minor) — Positive "reuse binding" scenario: RESOLVED

`runtime-binding-recovery/spec.md` now has a "A healthy binding is reused across normal
operations" scenario under the recovery requirement, positively asserting that task, retry,
Follow-up, Compact, model change, and Runner restart reuse a healthy binding without
resolve-probe or replacement. Format verified: `####` scenario with WHEN/THEN/AND.

### F4 (Minor) — Watchdog inconsistency: RESOLVED

Design D2 and the former "Open Questions" section no longer contradict. The "Resolved
Questions" section confirms: "The runner-disconnect → `unknown` server watchdog is included
in this change (T-001)." T-001's description includes the watchdog. "No open questions
remain."

## Full-plan verification

### Acceptance criteria → spec coverage

All 11 issue acceptance criteria map to spec requirements with scenarios (verified in the
first pass; AC4 now has a positive scenario). No gaps.

### Spec requirement → task coverage

All 17 requirements across the 3 capabilities are covered by at least one task:

- `agent-session-activity` (6 reqs): T-001 owns the activity model and transitions; T-004
  owns consumer-facing activity.
- `session-transcript` (5 reqs): T-001 owns event types and legacy-event removal; T-002
  owns `session.context_reset` writing on rebind.
- `runtime-binding-recovery` (6 reqs): T-002 owns CAS rebind and lineage removal; T-003
  owns recovery, non-recovery conditions, OpenCode Reset, and the unbound-candidate rule.

### Design decisions → task coverage

D1–D7 each map to a task. The "Resolved Questions" section closes all three formerly-open
items (signal granularity, OpenCode Reset, watchdog) with explicit decisions.

### Task graph

`tasks.json` is valid JSON; 4 tasks form a valid DAG
(T-001 → T-002 → T-003 → T-004). Every `dependsOn` references a strictly-lower-priority task.
Every task has acceptance criteria with test verification; `passes` is `false` on all. No
standalone test/move/rename tasks. `spec` anchors resolve to real requirements.

### Spec format

All three spec files use `### Requirement:` / `#### Scenario:` consistently. Every
requirement has at least one scenario. Zero malformed (3-hashtag) scenarios. No
`## ADDED/MODIFIED/REMOVED` delta headers. SHALL/MUST normative language throughout.

### Cross-artifact consistency

- Proposal's 3 capabilities match the 3 spec directories.
- Proposal's "What Changes" (BREAKING removals, activity model, recovery) matches the
  design decisions and task scope.
- Design's Resolved Questions match task descriptions (watchdog in T-001, OpenCode Reset
  in T-003).
- No contradictions between proposal, specs, design, and tasks.

## Verdict

All four first-pass findings are resolved. No new issues were introduced by the fixes. The
plan is internally consistent, all spec requirements have task coverage, and all issue
acceptance criteria have spec scenarios.

<promise>PASS</promise>
