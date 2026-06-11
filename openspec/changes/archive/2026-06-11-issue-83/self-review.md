# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` line 19 stated "the five commands that gain the option" but then listed six commands in parentheses (`project list`, `project show`, `issue list`, `issue show`, `issue workflow status`, `issue sessions`). The prose count was off by one.
  Verification: After edit, the proposal now reads "the six commands" and the parenthetical list still contains six entries. A parenthetical note was added to acknowledge the seventh `--output`-enabled command (`mo project repo list`) so the reader is not surprised by the design/scope, since `cli-project-repositories` and design D4 extend `--output` to that command.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: consistency
  Evidence: `design.md` D4 header read "the six `--output`-enabled commands" but the body of D4 lists seven commands (the six plus `project repo list`). The header number was off by one relative to the enumerated commands in the same decision.
  Verification: Header now reads "the seven `--output`-enabled commands" and the body enumeration is unchanged (still seven entries). The corresponding `TableShape` enum in T-006 also lists seven shapes (`ProjectList`, `ProjectShow`, `IssueList`, `IssueShow`, `WorkflowStatus`, `Sessions`, `RepoList`), so the design, spec (`cli-output-modes` + `cli-project-repositories`), and tasks all agree on seven.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue's Product Shape names six commands for `--output table|json` (the six high-use list/detail commands). The proposal + design D4 + `cli-project-repositories` spec deliberately add a seventh (`mo project repo list`), which is a small, consistent scope expansion (not in the issue, not in `cli-output-modes` spec). This is acceptable because the addition is fully consistent with the spec's "stable automation format = JSON; table is a presentation concern" rule and matches the project's "thin CLI wrapper" intent.
  SuggestedAction: A future issue or amendment could add a one-line note to the issue's Product Shape explicitly listing `mo project repo list` as a recipient of `--output`, or the change can be presented to the user with the implicit understanding that the repo `list` command benefits from the same ergonomic. No blocking action required for this issue.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-002 acceptance criterion #5 states that `ResolveProjectIdAsync(project: null, projectId: null)` with no active project "emits the standardized 'no active project' diagnostic" and T-003 introduces the `NoActiveProjectMessage` helper that produces that diagnostic. T-002's `dependsOn` is `["T-001"]` and T-003's `dependsOn` is `["T-002"]`, so T-002 ships before the helper exists. The intended sequencing is that T-002 produces a temporary hard-coded error string, and T-003 replaces it with the helper in both call sites (`MohistCliCommands.Issue.cs:43` and the resolver). The final behavior is correct; the test order is slightly forward-looking.
  SuggestedAction: Implementers should treat T-002 as scaffolding that uses a temporary literal string, then immediately replace it in T-003. A note could be added to T-002's `notes` field clarifying that the "standardized diagnostic" wording in the acceptance criteria describes the post-T-003 state. No structural change required.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-007 acceptance criterion "`mo issue show 83 --project mohist-local` sends `GET /api/projects/mohist-local/issues/83`" matches the existing `ProjectIssuesPath` URL format and the `ProjectResolutionEndpointFilter` route. This was spot-checked against `packages/server/src/Mohist.Server/Api/ProjectRoutes.cs:52` (which uses the same filter) and `IssueRoutes.Helpers.cs:29` (which references the filter). No change required.
  SuggestedAction: None. Just a verification note for reviewers.
  Status: follow-up

## Verification Notes

The following checks were performed against the artifacts and the existing code:

1. **Alignment to issue** — Every "What Changes" entry in `proposal.md` traces back to an issue Product Shape bullet or Acceptance Criterion. The 4 product-shape bullets and 9 acceptance criteria in the issue are all covered by at least one task.
2. **Completeness** — All 22 requirement headings (across 6 spec files) are referenced by at least one task in `tasks.json`. The full set of project-scoped issue subcommands (list, show, create, update, start, approve, reject, close, reopen, retry, rerun, force-stop, resume, rebase, archive, unarchive, logs, events, diff, commits, sessions, workflow status, workflow timeline) is split across T-007 (list/show/sessions/workflow status — also gains `--output`) and T-008 (the rest — gains `--project` only).
3. **Consistency** — Symbol names (`ProjectRefOption`, `OutputOption`, `BodyInputResolver`, `NoActiveProjectMessage`, `ResolveProjectIdAsync`, `PrintWithOutputAsync`, `RenderTableAsync`, `TableShape`, `BuildRepo`) are identical across proposal, design, and tasks. The standardized diagnostic string `Run 'mo project use <name-or-id>' or pass --project <name-or-id>` is identical across `cli-project-ref`, `cli-interface`, and `cli-project-repositories` specs, and the design D3 mandates the single-helper source of truth.
4. **Dependency soundness** — No cycles exist. T-001 → T-002 → T-003 form a linear chain; T-004 is independent; T-005 → T-006 form a linear chain; T-007, T-008, T-009, T-010, T-011 all depend on subsets of the prior tasks. All `dependsOn` entries reference existing IDs with lower priority numbers.
5. **Feasibility** — The server-side `ProjectResolutionEndpointFilter` (verified at `packages/server/src/Mohist.Server/Api/ProjectRoutes.cs:52`) and the four repository endpoints (`packages/server/src/Mohist.Server/Api/ProjectRoutes.cs:82-128`) already exist and are not modified. The test harness in `packages/server/tests/Mohist.Server.Tests/Specs/Project/Api/ProjectCliSpecs.cs:113` provides the `RecordingHttpHandler` + `FakeFileSystem` pattern that the four new spec files can extend.
6. **No circular dependencies** — Verified by walking the dep graph from each leaf task back to T-001.

<promise>PASS</promise>
