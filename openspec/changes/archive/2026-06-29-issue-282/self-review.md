# Self Review Report

## Result: PASS

## Repaired Items

_None._ No safe repairs were required. Every factual claim in `proposal.md` and `design.md` was verified against the live codebase and held up:

- `docs/epics.md:57` (field table lists `status` as `active / done / closed`), `:141` (legacy three-state lifecycle table), `:172` ("Epic 只是组织工具，不参与执行") — all present as quoted.
- `docs/concepts.md:82` ("一组相关 Issue 的集合"), `docs/web-ui.md:133` ("列出所有 epic"), `docs/cli-reference.md:196` (stale "当前 CLI 不支持的：Epic 管理" note), `docs/README.md:26` and `docs/getting-started.md:160` one-liners — all present as quoted.
- CLI subcommands claimed in `design.md` / `tasks.json` (`create`, `show`, `list`, `update`, `link`, `unlink`, `start`, `pause`, `resume`, `done`, `close`) match `packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs` exactly.
- Web UI labels claimed for the spec/tasks (`Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty`, `Start Epic`, `Pause`, `Resume`, `Mark Done`, `Start next issue`) match `packages/web/src/pages/epics/ui/EpicListPage.tsx` and `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx` verbatim.
- `design.md` claim that `EpicStatus` has no `Active` member and `parseEpicStatus('active') → Idle` is confirmed in `packages/web/src/entities/epic/model/types.ts:3-20`.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: completeness
  Evidence: Proposal "What Changes" entry #8 (spot-check `docs/README.md` and `docs/getting-started.md` one-liners) has no dedicated spec requirement/scenario. `T-005` borrows the `#epic-lifecycle-documentation-uses-the-five-state-self-driving-model` anchor (scoped to `docs/epics.md`) and `T-006`'s audit grep covers docs repo-wide, so the work IS actionable and covered by tasks — but a reader mapping spec→task for README/getting-started won't find an exact scenario.
  SuggestedAction: Optionally add a short scenario under an existing requirement (e.g. Requirement "Documentation and Web UI copy stay aligned with the self-driving model") stating that intro-pointer docs (`README.md`, `getting-started.md`) SHALL NOT frame Epic purely as a static organizer. Not blocking: `T-005` acceptance criteria already capture the behavior.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `proposal.md` "What Changes" #6 and `design.md` Goals list the list-page groups as "(Running / Ready to start / Waiting / Blocked / Idle / Empty)" — a 6-item slash list that reads ambiguously vs. the actual 4 UI groups (`Waiting / Blocked` and `Idle / Empty` are each a single combined group). The spec (Requirement "Web UI documentation describes the self-driving Epic surfaces") and `T-003` capture the correct 4-group structure with exact labels, so the contract is right; only the proposal/design shorthand is imprecise.
  SuggestedAction: No change needed for correctness (spec is the contract). If desired, tweak the proposal/design parentheticals to "(Running / Ready to start / Waiting / Blocked / Idle / Empty, where the last two are combined groups)".
  Status: follow-up

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: `T-001` acceptance criteria cover spec Requirements 1–4 (five-state model, Start/Pause/Resume, auto-advancement/running-but-idle, Epic↔workflow relationship), but its `spec` anchor points only to Requirement 1. The `spec` field schema is a single string (file + deep link), so multi-anchor referencing isn't supported; the task description clearly enumerates the full scope.
  SuggestedAction: None — the file reference is correct and the description/acceptance criteria make the full coverage explicit.
  Status: follow-up

## Verification Summary

- **Alignment**: Every issue Acceptance Criterion (AC1 remove legacy three-state; AC2 document five states + entry conditions; AC3 document Start/Pause/Resume + running-but-idle/`nextIssueReason`; AC4 Epic↔workflow relationship; AC5 UI copy `Active`/`Start`/`No linked issues` consistency; AC6 doc examples match Web UI paths) traces to a proposal "What Changes" entry, a spec Requirement with scenarios, and at least one task with acceptance criteria. All three issue Non-Goals (no new tutorial site, no internal impl details, no lifecycle behavior change) are reflected in `design.md` Non-Goals and Decision 1/4.
- **Completeness**: All 8 spec Requirements map to tasks (Req 1–4 → T-001; Req 5 → T-002; Req 6 → T-003; Req 7 → T-004; Req 8 → T-006). All 6 tasks map to a spec file. Edge cases (idempotency, non-interrupting Pause, default idle not active, terminal done/closed, running-but-idle not a sixth state, internal-symbol names out of scope) are explicitly addressed.
- **Consistency**: Spec aligns with proposal Capabilities (single new capability `epic-docs` ↔ `specs/epic-docs/spec.md`). Design Decisions 1–5 align with specs and tasks. Naming (`idle`/`running`/`paused`/`done`/`closed`, `Start Epic`, `nextIssueReason`) is consistent across issue → proposal → spec → design → tasks and matches the codebase.
- **Feasibility**: No new runtime deps; docs edits depend only on the frozen 278/279/281 surface (verified present). CLI examples derivable from `packages/cli`; HTTP examples derivable from `Api/EpicRoutes.cs`. Task granularity is appropriate — each task is a complete doc/copy slice, no task is a pure rename/file-move/DI-registration, and no separate "add tests" task exists (Web tests are bundled into T-006's edit-on-find verification).
- **Dependency completeness**: `T-001` has no deps (priority 1). `T-002`/`T-003`/`T-004` depend on `T-001` (priority 2 > 1). `T-005` depends on `T-001` (priority 3 > 1). `T-006` depends on `T-001`–`T-005` (priority 4 > all). Every `dependsOn` ID exists and points to a lower-priority task. No cycles.

<promise>PASS</promise>
