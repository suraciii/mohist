# Self-Review — issue-511 (mechanical-debt cleanup)

Reviewer mode: read-only. Findings below are problems for a separate fix task; this review does not modify any artifact other than this file.

## Verdict

The bulk of the plan is sound: the four capabilities in `proposal.md` each have a matching spec directory, every spec requirement has ≥1 `#### Scenario:` (4 hashtags, WHEN/THEN), SHALL/MUST language is used, no `## ADDED/MODIFIED/REMOVED` headers, `tasks.json` is valid JSON with a sound DAG (only edge `T-005 → T-004`), and the design's decisions are grounded in verified code. However, four problems must be fixed before build — the most serious is a broken task↔spec contract for the Group E work.

## Findings (must fix)

### F1 — T-006's `spec` field points at an unrelated requirement (misattributed task↔spec contract)

`tasks.json` T-006 (`EventDispatcherService.Backoff` → private) references:
`specs/workflow-grain-production-contract/spec.md#profile-resolution-failure-classified-by-exception-type-not-message-text`

That requirement is about typing the profile-resolution exception. It has nothing to do with `Backoff` visibility or retry-cadence observation. The task notes even half-acknowledge this ("belongs to the same internal-API-surface tightening theme"), but a thematic resemblance is not a spec contract. Per the tasks schema, `spec` is "Reference to a spec requirement **when applicable**"; for a task with no applicable requirement the field should be empty, not pointing at an unrelated one. As written, a builder reads T-006, follows the link, and finds a requirement that does not cover the work.

### F2 — Group E (Backoff, ResolveLayeredVariablesAsync inline) has no spec coverage anywhere

The proposal deliberately folds Group E into Impact as "micro-cleanups" with no capability, and no spec file describes their required behavior. Consequences:

- T-006 (Backoff) has no spec backing at all (see F1 — the reference it does have is wrong).
- T-002 bundles the `ResolveLayeredVariablesAsync` inline, but `specs/workflow-run-variables-store/spec.md` only covers the Store rename, method wording, and the table decision. No requirement in that spec describes deleting the pass-through wrapper or switching the 7 spec call sites. So a portion of T-002 is also unbacked by any spec.

This contradicts the proposal's own contract ("Each capability listed here will need a corresponding spec file") and the tasks instruction ("Reference specs for what needs to build"). Either Group E needs a minimal capability spec (e.g. `internal-api-surface`), or T-002/T-006 must drop the inline/Backoff sub-work to tasks whose `spec` field is honestly empty, or those sub-items need explicit requirements added to an existing spec. The current state — tasks that claim spec coverage they do not have — is the defect.

### F3 — `status-wire-mapping` web-union requirement is under-specified and potentially mandates an unflagged web change

The spec requirement "Web status unions mirror their authoritative server enums" requires each of the four web unions to (a) include every wire value its authoritative server enum emits and (b) carry a comment naming that enum. But neither the spec nor `design.md` D3 resolves **which server enum is authoritative** for the three non-trivial unions, and at least one currently diverges:

- `WorkflowStageRunStatus` (web) = `pending|running|awaiting-approval|passed|failed|skipped`. The likely authoritative `StageRunStatus` (server) emits `pending|running|awaiting-approval|completed|failed` — i.e. the web union is **missing `completed`** and carries `passed`/`skipped` (not in `StageRunStatus`). Taken literally, the spec's "must include every wire value its authoritative server enum emits" would *force adding `completed` to the web union* — a web-type change that is nowhere acknowledged in the proposal's "external behavior unchanged / wire values preserved" contract or in any task.
- `WorkflowRecoverySummary` = `running|awaiting-approval|waiting-for-recovery|completed`. `waiting-for-recovery` is a client-side projection, not a value of any single server enum, so "name its authoritative server enum" is not cleanly satisfiable for this union.

The plan needs to either (a) pin the exact union→enum mapping per union and reconcile divergences explicitly (including deciding whether `completed` must be added to the web union, which may expand scope), or (b) weaken the requirement to "each union names the server enum it *primarily* tracks and may carry additional client-only states" without the completeness obligation that currently forces a change. As written, an implementer cannot satisfy the requirement without making an undocumented scope decision.

### F4 — Cross-artifact enum-name inconsistency: `CheckRunStatus` (non-existent) vs `StageCheckStatus` (real)

`proposal.md` and one line of `design.md` use `CheckRunStatus` for the fourth status enum; `specs/status-wire-mapping/spec.md` and the rest of `design.md` use the correct `StageCheckStatus` (verified: the codebase enum is `StageCheckStatus` at `packages/server/src/Mohist.Server/Workflow/Domain/Run/StageCheck.cs:5`; the issue body's `CheckRunStatus` is wrong). The proposal is supposed to be the contract that the specs refine; using a name that does not exist in the code there is an accuracy defect and will confuse a builder grepping for `CheckRunStatus`. All artifacts should use `StageCheckStatus`.

## Minor observations (not blocking, but worth a fix task's attention)

- **M1 — `WorkflowRunProfileManager` reference count unverified.** Proposal/design state "14 files / 32 references"; the "14 files" was confirmed via `rg -l`, but the "32 references" figure was taken from the issue body without independent verification. The acceptance criterion (zero matches post-rename) is what matters, so this is cosmetic, but the count should be checked or hedged.
- **M2 — T-002 has no `dependsOn` on T-001 despite file overlap.** Both edit `WorkflowGrain.cs` and `WorkflowProfileManager.cs`. Per the tasks rule (`dependsOn` = consumes prior output) this is correctly empty since the rename consumes nothing from the dead-path removal — priority ordering handles sequencing for an AFK agent. Flagging only because the overlap is real and a careless parallel application could conflict; the notes already acknowledge this.
- **M3 — Comment-ban C# scope.** The ArchTest scans `ServerSources/` (`Mohist.Server`) only. The CLI is also C# and the ArchTests project references it, but the issue's 38 offenders are server-side and the Non-Goal excludes cli cleanup, so server-only is defensible. Worth a one-line note in the spec that CLI C# is intentionally out of scope to prevent a future "why doesn't the ban cover the CLI?" question.

## What is correct and need not change

- Capability→spec-directory mapping is 1:1 and names match exactly (verified).
- Spec format compliance: all requirements use `### Requirement:`, all scenarios use exactly `#### Scenario:` with WHEN/THEN, every requirement has ≥1 scenario, normative SHALL/MUST throughout, no delta-operation headers.
- `tasks.json` is valid JSON; every task has all required fields; DAG is acyclic; the sole dependency (`T-005 → T-004`) is a true output dependency and points to a strictly lower priority.
- Design decisions cite verified code locations (dead `On` switch at `WorkflowGrain.cs:644-667`, `BindProfileForTest` at `:60`, the `Contains("no current definition")` match at `:624-626`, the already-embedded `ServerSources/` plumbing and `Microsoft.CodeAnalysis.CSharp` reference that enable the comment-ban ArchTest).
- The two-phase comment-ban split (T-004 establish ratchet, T-005 clear to hard ban) is well-motivated and matches the design.

<promise>FAIL</promise>
