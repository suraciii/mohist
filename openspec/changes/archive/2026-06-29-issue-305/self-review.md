# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Task T-004 and design D4 specify adding a mutual-exclusivity validation rejecting `mo issue archive 42 --all-completed` (currently the flag silently wins over the positional). The spec requirement "CLI issue archive batch-archives all completed issues" had no scenario for this behavior, so spec ↔ task/design were inconsistent. Design's Open Questions even conceded "the spec does not explicitly require rejecting". Added the "Single-issue archive and batch flag are mutually exclusive" scenario to `specs/cli-interface/spec.md` under the archive requirement so the spec, design D4, and T-004 acceptance criteria all describe the same contract.
  Verification: Re-read the archive requirement block; the new scenario sits with the other archive scenarios and states the WHEN/THEN mirroring T-004's acceptance criterion (`<number>` 与 `--all-completed` 同时传入 → 非零退出 + 清晰消息). No other artifact touched.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design D5 / Open Question flags that the project `/workflow-profile` payload shape must be verified against the issue-level profile at implementation time to decide whether `RenderWorkflowProfile*` can be reused or a thin project renderer is needed. T-002 notes capture this. This is a legitimate implementation-time check, not a plan defect, so it is tracked as follow-up rather than repaired.
  SuggestedAction: During T-002 implementation, diff `GET /api/projects/{id}/workflow-profile` against the issue-level shape and record the decision in the commit/task notes.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Design D6 says "extend `CliIssueCommandSpecs.cs`" but no such file exists today (issue test files are topic-split: `CliIssueSessionSpecs.cs`, `CliIssueWorkflowConfigSpecs.cs`, etc.). T-004's `output` field correctly implies creating the new file. The wording drift ("extend" vs "create") is harmless since the output field is unambiguous.
  SuggestedAction: Treat T-004 as creating `CliIssueCommandSpecs.cs`; no artifact change needed.
  Status: follow-up

## Review Notes

- **Alignment**: Every issue Acceptance Criterion maps to spec requirements and tasks — AC1 (template CRUD) → T-001; AC2/AC3 (default template + variables view/replace/merge + prompts) → T-002; AC4 (session followup) → T-003; AC5 (batch archive) → T-004; AC6 (docs) and AC7 (tests) are embedded in each task's acceptance criteria. All Non-Goals (no server/Web/metrics/inbox/read-only changes) honored; verified all backing endpoints already exist in `ProjectRoutes.cs`, `IssueRoutes.Sessions.cs`, `IssueRoutes.Lifecycle.cs`.
- **Completeness**: All 11 ADDED/MODIFIED spec requirements have owning tasks; edge cases (404 surfacing, mutual exclusivity of `--vars-file` vs `--var`, no-flags non-zero exit, `session_inactive`/`runner_offline`/unknown-session, invalid YAML, no-resolvable-project) are covered by scenarios.
- **Feasibility**: Tasks are complete feature slices (template subgroup, config subgroup, session followup, archive formalisation) — none are over-decomposed technical actions, code moves, or standalone test/doc tasks (tests and docs live inside each slice). Precedent line numbers cited in design verified accurate (`BuildArchive:749`, `BuildSession:854`, `BuildSessionFollowup:450`, `BuildWorkflowConfig:1122`). Confirmed zero existing test coverage for archive, matching the proposal.
- **Dependencies**: T-002 `dependsOn: [T-001]` (config mounts on the workflow skeleton) with priorities 1→2 correctly ordered; T-003 and T-004 both edit `MohistCliCommands.Issue.cs` but touch disjoint command builders (BuildSession vs BuildArchive) and are functionally independent — the file overlap is flagged in T-003 notes for integration. No cycles; all `dependsOn` targets exist with lower-or-equal priority.

<promise>PASS</promise>
