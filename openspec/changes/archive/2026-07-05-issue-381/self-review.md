# Self Review Report

## Result: PASS

## Repaired Items

_None — no safe repairs were required. The artifacts are internally consistent and
fully trace to the issue._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: The `spec` references in `tasks.json` use descriptive key-phrase
  anchors (e.g. `#same-grain-method-and-state-guards`,
  `#the-show-read-model-carries-associated-issue-context`) rather than the exact
  GitHub-slug of each `### Requirement:` header. The references are unambiguous
  and map 1:1 to a requirement section in every case, so navigation is not
  impaired, but they are not byte-exact slugs.
  SuggestedAction: If the openspec validator enforces literal slug matching,
  regenerate the anchors from the requirement titles; otherwise leave as-is
  since the convention is stable and intentional across all six tasks.
  Status: follow-up

## Review Notes

### Alignment

Every acceptance criterion in the issue traces to a task + spec:

- Naming decision (Plan A) + profile sink + `design/cli.md` principle ->
  T-003 + T-006 + `workflow-profile-relocation/spec.md`.
- 8 control actions by `workflowRunId` -> T-001 (server endpoints) + T-004
  (CLI). The 7-verb surface (`approve/reject/retry/rerun/resume/pause/stop`)
  covers all 8 legacy actions because `rerun-from-stage` collapses into
  `rerun --from-stage` and `force-stop` renames to `pause` — both collapses are
  explicitly blessed by the issue body.
- Read gap (`show`/`status`/`variables`/`events`/`list-sessions`) ->
  T-002 + T-005 + `workflow-run-reads/spec.md`.
- "No command for an output format" (`show -o yaml`, no `yaml` command) ->
  encoded as a hard requirement in both the control/reads specs and T-005
  acceptance.
- `sessions` scope limit (list only; no single-session workflowRunId entry) ->
  explicit requirement + T-005 acceptance.
- Task-injection Tier adjudication recorded -> Decision 5 + T-006 acceptance.
- `agent-subscriptions.md` prerequisite closed -> T-006 acceptance (the `show`
  read model carrying associated issue number + title satisfies the
  `mo workflow get <runId>` prerequisite; the naming reconciliation `get`->
  `show` is documented).
- Shared-factory conventions, CLI tests, `docs/cli-reference.md` update ->
  encoded in every implementation task's acceptance criteria.

No issue requirement is missing or misinterpreted.

### Completeness

- All three Capabilities (`workflow-run-control`, `workflow-run-reads`,
  `workflow-profile-relocation`) have dedicated spec files.
- Every spec Requirement is exercised by at least one task acceptance
  criterion.
- Edge cases covered: empty/whitespace `--message` and `--from-stage` (local
  reject, no request), unknown/not-reached stage structured error passthrough,
  ActiveOnly vs RetryOrRerun admission, issue-join miss (null ref, not an
  error), single-session sub-action absence, profile-relocation fallback and
  `--project`/`--project-id` conflict.

### Consistency

- Spec capabilities map 1:1 to proposal Capabilities.
- `design.md` Decisions 1-8 map cleanly onto spec Requirements
  (D1->same-grain-method-and-state-guards; D2->associated-issue context;
  D3->status compact projection; D4->rerun --from-stage; D5->task-injection
  deferral; D6->profile relocation; D7->design/cli.md; D8->shared factory).
- Naming (`workflowRunId`, `WorkflowRun`, `WorkflowProfile`) is uniform across
  proposal/design/specs/tasks.
- Server retains two endpoints (`rerun` / `rerun-from-stage`) while the CLI
  collapses them — this split is stated consistently in D1, D4, T-001 notes,
  and T-004 acceptance.

### Feasibility

- T-001/T-002/T-003 are independent foundation slices (server control
  endpoints, server read model, CLI profile relocation).
- T-004/T-005 are complete CLI feature slices (control group / read group)
  with tests bundled in (no separate "add tests" task).
- T-006 is the single docs slice.
- No task is over-granular: none is titled "define interface" / "extract
  class" / "register DI" / "create file" / "add tests"; none is a pure
  rename/move without function (T-003 is a path relocation but bundles
  behavioral parity verification and test migration, so it is a complete
  migration slice).

### Dependency Completeness

- T-001, T-002, T-003: `dependsOn: []`, priority 1.
- T-004: `dependsOn: [T-001, T-003]`, priority 2 — both deps priority 1.
- T-005: `dependsOn: [T-002, T-003]`, priority 2 — both deps priority 1.
- T-006: `dependsOn: [T-003, T-004, T-005]`, priority 3 — all deps lower.
- All `dependsOn` IDs exist; no cycles; priorities strictly decrease along
  every edge.

<promise>PASS</promise>
