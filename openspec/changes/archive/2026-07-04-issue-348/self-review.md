# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: The `agent-archive` spec defines three Archived-list-section scenarios ("Archived agents appear in the
  Archived section", "Archived rows are visually distinct but navigable", "Empty Archived section is omitted").
  T-002 makes the data flow (`useAgents` → `all: true`) and the section code already ships in `AgentListPage`, but
  T-002's acceptance criteria only asserted the data-layer change and the composer filter — no task owned a list-page
  rendering test for those spec scenarios. Added one acceptance criterion to T-002 requiring a list-page component test
  covering all three Archived-section scenarios (population with correct count, distinct+navigable rows, omission when
  empty). T-002 is the natural home since it is the task that populates the dormant section.
  Verification: `tasks.json` re-parsed as valid JSON; T-002 acceptance count went from 7 to 8; the new criterion maps
  1:1 to the `agent-archive` spec scenarios at `specs/agent-archive/spec.md:19-31`.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: T-002 and T-003 each implement requirements drawn from BOTH spec files (T-002: agent-unarchive web client
  + agent-archive list visibility; T-003: agent-unarchive detail affordance + agent-archive Actions-button honesty),
  but the `spec` field references only the primary spec each. This matches the codebase convention — every archived
  `tasks.json` uses a single-string `spec` field (verified across issues 345/338/336/330), and both tasks do describe
  the cross-spec work in their `description`/`acceptanceCriteria`/`notes`. So traceability is present, just not via
  the `spec` pointer.
  SuggestedAction: If a multi-spec pointer format is ever introduced, update T-002 and T-003 to list both spec anchors.
  No change needed under the current single-string convention.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Design D7 has `useUnarchiveAgent.onSuccess` invalidate `['agent-status']` in addition to `['agents']`
  (mirroring `useArchiveAgent`), but the `agent-unarchive` spec only mandates invalidating `['agents']`. The design
  exceeding the spec floor is acceptable (spec is a minimum), and T-002 enforces the stronger behavior.
  SuggestedAction: Optionally strengthen the `agent-unarchive` web-client requirement to also name `['agent-status']`
  so spec and implementation match exactly.
  Status: follow-up

## Notes

- **Alignment**: Proposal selects option (a) (true reversible archive), matching the issue's medium/medium sizing and
  its AC. Every "What Change" traces to an issue requirement; both false-claim problems ("can be reversed",
  "remain visible") and the Actions label/behavior mismatch (issue problem 2) are explicitly owned by T-004 and T-003
  respectively. Both issue Non-Goals (no third `deleted` state; archived-cannot-launch preserved) are restated as
  design Non-Goals and enforced in T-001/T-003.
- **Feasibility / granularity**: All four tasks are complete feature slices (server lifecycle, web data layer,
  detail-page Actions card, editor confirmation copy). None is a pure refactor, rename, DI-registration, install
  split, or standalone test-only task; tests are bundled into each implementing task.
- **Dependency completeness**: Linear DAG T-001→T-002→T-003→T-004; every non-root task depends on an existing
  lower-priority ID; no cycles. T-001 ships the route the web depends on; T-003 ships the affordance that makes
  T-004's reversibility wording honest at every commit.

<promise>PASS</promise>
