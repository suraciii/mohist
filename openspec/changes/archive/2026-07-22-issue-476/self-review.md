# Self-Review: issue-476

## Verdict: PASS

The plan is well-structured, internally consistent, and technically sound. All
eight acceptance criteria have spec coverage and task assignments. The non-goals
are respected throughout. The few findings below are non-blocking clarifications
a builder can resolve during implementation.

---

## Findings

### F1 (non-blocking): Spec implies `mo workflow --help` produces output; design removes the command entirely

**Where:** `specs/issue-start-binding/spec.md` — scenario "Workflow help does not
list execution subcommands" says `mo workflow --help` output SHALL NOT list
execution verbs. Design D7 removes `WorkflowCommands.Build` from root entirely.

**Impact:** If the `workflow` command is completely removed, `mo workflow --help`
fails to resolve rather than producing an empty subcommand listing. The scenario
is vacuously satisfied (no listing means no forbidden verbs in the listing), but
a builder reading the spec literally might expect the command to still exist.

**Resolution for builder:** Follow the design (D7) — remove `workflow` entirely.
The spec scenario's intent ("execution verbs are not reachable via `workflow`")
is satisfied either way. The `CliRootCommandShapeTests` update (remove `workflow`
from `survivingResourceGroups`) is the authoritative test.

### F2 (non-blocking): `run list` / `run view` --json field names in specs are illustrative, not final

**Where:** `specs/run-reads/spec.md` — scenarios use `id`, `status`,
`currentStage`. The existing `WorkflowRunDetail` descriptor is
`["status", "issueRef"]`; `run list` derives from issues whose fields are
`["number", "title", "status", "stage", ...]`.

**Impact:** A builder following the spec literally would add `currentStage` to a
descriptor, but the API field may be named `stage`. The exact field names need
to be finalized during implementation.

**Resolution for builder:** Design Open Questions already acknowledges this.
Define the descriptor fields from the actual API response shapes. The spec
scenarios are testing the *capability* (field selection works), not pinning
specific field names.

### F3 (non-blocking): T-002 and T-003 both modify `RunCommands.Build()` in the same file

**Where:** `tasks.json` — T-002 (reads) and T-003 (feedback) both depend only on
T-001 and both need to register subcommands in `MohistCliCommands.Run.cs:Build()`.

**Impact:** If the runner executes T-002 and T-003 in parallel, both modify the
same method. The priority ordering (2 before 3) likely serializes them, but this
is not explicitly guaranteed.

**Resolution for builder:** If parallel execution occurs, the second task simply
adds its subcommands to the `Build()` method after the first task's changes are
committed. No architectural issue — just a potential merge conflict that
sequential priority ordering avoids.

---

## Verification Summary

### Acceptance criteria coverage

| AC | Spec | Task |
|---|---|---|
| `issue start` returns Run ID | issue-start-binding ✓ | T-004 ✓ |
| `run list/view/watch` with Run ID + `--issue` | run-reads ✓ | T-002 ✓ |
| Control verbs only in `run` | run-control ✓ | T-001 + T-004 ✓ |
| `retry` vs `rerun` distinct | run-control ✓ | T-001 ✓ |
| `pause` resumable / `stop` terminal + `--yes` | run-control ✓ | T-001 ✓ |
| `run feedback list/view` | run-feedback ✓ | T-003 + T-004 ✓ |
| `issue` retains CRUD, no control | issue-start-binding ✓ | T-004 ✓ |
| Old `workflow` + issue aliases removed | issue-start-binding ✓ | T-004 ✓ |

### Non-goals respected

- No WorkflowProfile collection management ✓ (design D7 removes workflow command; Profile management deferred)
- No AgentSession migration ✓ (design D8 retains `issue session/sessions`)
- No Variable CLI ✓ (not in any task or spec)
- No `show`/`update` renames ✓ (design D8 retains them explicitly)

### Technical feasibility verified against codebase

- Server run-scoped endpoints confirmed: `/api/workflow-runs/{id}/{approve,reject,retry,rerun,rerun-from-stage,resume,pause,stop}` ✓
- `GET /api/workflow-runs/{id}` returns `WorkflowRunDetailDto` with `issueRef` (number + title) ✓ — needed for D9 feedback resolution
- No `GET /api/workflow-runs` list endpoint — D3 derivation from issues list is the correct approach ✓
- Events endpoint returns bounded JSON array, not SSE — D4 polling approach is correct ✓
- `CliInvocation.PromptsEnabled` exists and works for D5 stop confirmation ✓
- All test files referenced in design Test Impact exist in the codebase ✓
- `IssueDescriptor` currently lists `workflowRun` (wrong field name) — D6 fix to `workflowRunId` is needed and correct ✓
- Issue #475 shared contracts (`--project`, `--json`, exit codes) are in place ✓

### Task DAG validation

- 4 tasks, valid acyclic graph ✓
- Every `dependsOn` references a strictly lower priority task ✓
- Every task has acceptance criteria including test verification ✓
- Every task is independently deliverable (feature module in usable state after completion) ✓
- No standalone test tasks (tests embedded in each implementation task) ✓

### Spec format validation

- All specs start with `### Requirement:` blocks (no ADDED/MODIFIED/REMOVED headers) ✓
- All scenarios use exactly 4 hashtags (`####`) ✓
- All requirements use SHALL/MUST normative language ✓
- Every requirement has at least one scenario ✓
- Specs are self-contained (no cross-references between spec files) ✓

<promise>PASS</promise>
