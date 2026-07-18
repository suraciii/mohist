# Self-Review — issue-418 (子 issue 父子关系模型)

Reviewed artifacts: `proposal.md`, `specs/issue-parent-child/spec.md`, `design.md`, `tasks.json` against the issue body and the live codebase. No artifact other than this file was modified.

## Verification against the live code

Code-level claims were checked by reading the actual sources, not just the explore summary:

- **Design Decision 3 (Epic isolation via an `Issue.AssignEpic` self-invariant) is correct.** `EpicGrain.LinkIssueAsync` (`EpicGrain.cs:69`) reads the issue row for the idempotency short-circuit, then calls `target.AssignEpicAsync(epicNumber)` on the Issue grain (`EpicGrain.cs:86`). The batch path `LinkIssuesAsync` calls the same `AssignEpicAsync` per item (`EpicGrain.cs:121`). Because the link routes through Issue.`AssignEpic`, a self-invariant there (`throw when _parentIssueNumber is not null`) enforces the invariant on every Epic-link path, and the Epic domain/grain logic genuinely stays untouched. Confirmed consistent with `design/issue-breakdown.md:41`.
- **Design Decision 2's guard `!HasWorkflowStarted && _status == Backlog` is the correct composite.** `Issue.Close` (`Issue.Transitions.cs:275`) sets `Cancelled` without ever starting a workflow, so `_status == Backlog` is required in addition to `!HasWorkflowStarted` — a closed-but-never-started issue must still be rejected. The design and T-001 both state this exact conjunction.
- **T-002 placement is accurate.** `IssueGrain.StartWorkAsync` (`IssueGrain.cs:145`) gathers `undeliveredPrerequisites` (`:153`) and passes them to `ThrowIfStartBlocked` (`:154`) → `Issue.StartBlocker(...)` (`Issue.Transitions.cs:160`). Adding a `bool hasChildren` parameter follows the identical gather-then-decide pattern.
- **T-001 create-path placement is accurate.** `IssueRoutes.Crud.cs:40` resolves the repository (`:78`) and mints the number (`:83`) before invoking the grain via the coordinator; parent resolution + priority inheritance slot in alongside repository resolution, as the design states.
- **T-003 list-filter placement is accurate.** The list route (`IssueRoutes.Crud.cs:21-38`) forwards filters to `IssueQuerier.ListWithLabelFiltersAsync`; a `parentIssueNumber` parameter follows the same shape.
- **Referenced migration exists:** `20260715000000_BackfillIssueEpicAffiliation.cs` (and `20260716165000_MigrateEpicAffiliationToIssues.cs`), so the "additive column + null backfill, paralleling the Epic affiliation migration" analogy in Design Decision 6 / T-001 is grounded.

## Coverage

**Issue acceptance criteria → tasks** — all 8 covered, none missing:

| Acceptance criterion | Covered by |
|---|---|
| `--parent` create shows relationship on both details | T-001 (child back-ref) + T-003 (projection) |
| attach backlog issue / `--parent none` / start after full detach | T-001 (attach/detach) + T-002 (start-after-detach) |
| parent with children start refused | T-002 |
| in-workflow issue can't be split or attached | T-001 (`!HasWorkflowStarted && Backlog` guard) |
| grandchild attach refused | T-001 (single-level: parent-not-a-child guard) |
| sub↔Epic isolation both directions | T-001 (`AssignEpic` + `AssignParent` self-invariants) |
| priority inheritance | T-001 (create orchestration) |
| `mo issue list --parent 42` | T-003 |

**Spec requirements → tasks** — all 10 requirements map to at least one task; every requirement has ≥1 scenario; scenario hashtag depth (`####`) verified.

**Tasks DAG** — `tasks.json` is valid JSON; T-002 and T-003 both depend only on T-001; priorities (1/2/3) are strictly greater than each task's dependencies. Acyclic.

**Internal consistency** — no contradictions between proposal/spec/design/tasks. The `--parent`/`--parent none` surface, the single-level model, the Epic-isolation rule, the derived-fact model, and the start refusal are stated identically across all four artifacts. Scope discipline holds: composite advancement, status aggregation, and the parent close/archive constraints from `design/issue-breakdown.md`'s lifecycle table are consistently excluded everywhere and left to later issues.

## Findings (none block building; listed for the implementation task to pick up)

1. **Epic batch-link route catch (minor, cosmetic).** The invariant itself is enforced structurally — `Issue.AssignEpic` throws for any child, so a sub-issue can never join an Epic via any path. The single-link route (`EpicRoutes.cs:66-79`) is where T-001 maps the new exception to a typed 409. The batch-link route (`EpicRoutes.cs:96` → `BatchLinkRouteAsync`) is not mentioned in T-001's acceptance criteria; without a matching catch there, a sub-issue in a batch would surface as a raw 500 rather than a clean per-item 409. Recommend T-001's acceptance criteria explicitly require both the single and batch Epic-link routes to map the rejection to the typed code. Not a correctness gap in the invariant (the child still does not join the Epic), only in the HTTP status surfaced.

2. **Draft eligibility is unstated.** The guard `!HasWorkflowStarted && _status == Backlog` makes a draft issue (Backlog, not started) eligible to be either a parent or a child. This is consistent with `design/issue-breakdown.md`'s "子必须 backlog 未启动" (drafts are not excluded) and is harmless (draft only blocks starting; a parent cannot start anyway), but neither the spec nor the tasks say so. Recommend a one-line note in T-001 so the implementer does not add an unintended `!IsDraft` clause.

3. **T-003 ordering note.** T-003 is priority 3 but depends only on T-001, not T-002 (correctly noted in its own notes). If parallelism is desired, T-002 and T-003 could run concurrently after T-001; the current linear priorities are a hint, not a constraint. No change needed.

## Verdict

The plan is internally consistent, grounded in the actual code (verified at the cited call sites), covers all eight issue acceptance criteria and all ten spec requirements, has a valid task DAG, and respects the issue's Non-Goals. The three findings are refinements within T-001's existing scope, not structural defects — the core design (single-direction child ref, derived parent fact, `AssignEpic` self-invariant for Epic isolation, `StartBlocker` extension for parent refusal) is verified correct and ready to build.

<promise>PASS</promise>
