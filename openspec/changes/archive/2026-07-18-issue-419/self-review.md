# Self-Review — Issue #419 (复合推进与状态汇总)

Reviewer role: critical reviewer. Findings only; no fixes applied here.

## Coverage of issue acceptance criteria

All seven acceptance criteria in the issue body map to spec requirements:

| AC | Spec coverage |
|---|---|
| 启动父 issue，无 prerequisite 子 issue 并行启动；有 prerequisite 子 issue 在依赖到达后自动启动 | compound-advancement req 1, 2, 4 (sibling prereq only) |
| 全部子 issue 终态且 ≥1 done → 父 done；全部 cancelled → 父 cancelled | parent-status-aggregation req 1 |
| close 父 issue 要求全部子 issue 已到终态 | parent-status-aggregation req 2 |
| 归档父 issue 时子 issue 一并归档 | parent-status-aggregation req 4 |
| 子 issue 被 reopen 后，已 done 的父 issue 自动回到 in-progress | parent-status-aggregation req 6 |
| Epic link 父 issue：Epic 轮到它时启动它并触发复合推进，它 done 时计入 Epic 进度 | compound-advancement req 7 |
| 复合推进启动的子 issue 数量不突破项目并发上限 | compound-advancement req 3 + design D10 |

Spec → task coverage mapping in `tasks.json` is consistent: full compound-advancement spec in T-002; parent-status-aggregation requirements split between T-002 (1, 5, 6, 7) and T-003 (2, 3, 4).

## Problems found

### P1 — External-prerequisite recompute is not covered (BLOCKER)

**Where.** Spec `compound-advancement/spec.md` req 4; design D5; task T-002.

**Problem.** The spec scenario "A child whose prerequisite is a sibling starts when the sibling completes" only covers intra-parent (sibling) prerequisites. There is no spec scenario and no design handler for the case where a child's prerequisite is an *external* issue (not another child of the same parent). When that external issue completes, no event from the parent's perspective triggers a recompute — `IssueCompositeChildTerminalHandler` only fires on the parent's own children, and the `parent` lineage stamp on the completing event points at the external issue's parent (or nothing), not at this parent.

This directly violates the issue's first acceptance criterion: "有 prerequisite 的子 issue 在依赖到达后自动启动" — when the prerequisite is external, the child stays Backlog indefinitely.

The Epic subsystem already solves this exact case: `EpicAutoDoneHandler.DispatchAsync` carries `includePrerequisiteLookup: true` and `EpicQuerier.GetEpicNumbersDependentOnPrerequisiteAsync` reverse-looks-up epics whose members depend on the completed issue (`packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs:45,313-320`, `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:188`). The design claims to "parallel `EpicAutoDoneHandler` and friends" but omits this leg of the parallel.

**Required fix.**
- Add a spec scenario under compound-advancement req 4: "A child whose prerequisite is an external (non-sibling) issue starts when that external issue completes."
- Extend design D5 with a fifth handler leg that, on `com.mohist.issue.completed`, reverse-looks-up parents whose children list the completed issue as a prerequisite (mirroring `GetEpicNumbersDependentOnPrerequisiteAsync`), and dispatches `RecomputeCompositeStatusAsync` to those parents. Document that the lookup lives in `IssueQuerier` next to the Epic equivalent.
- Add corresponding acceptance criterion to T-002.

### P2 — Reopen of an aggregated-Cancelled parent is not durable against re-aggregation (BLOCKER)

**Where.** Spec `parent-status-aggregation/spec.md` req 1, req 3; design D3, D6; task T-003.

**Problem.** Per req 1, "all children Cancelled → parent Cancelled" is an automatic aggregation rule. Per req 3, `mo issue reopen <parent>` on a Cancelled parent returns it to Backlog and "SHALL NOT modify any child's state". So after the user reopens P, P is Backlog and all its children are still Cancelled. Now any subsequent recompute (e.g. triggered by `IssueCompositeParentChangedHandler` on an unrelated attach, or any child event) re-applies the aggregation table: "all Cancelled → Cancelled" — and P flips back to Cancelled, defeating the user's reopen.

Neither the spec nor the design resolves this contradiction. The design's `RecomputeCompositeStatus` decision method mechanically applies the four-state table, and D6 says "if `target != _issue.Status`, apply the matching transition". So the recompute would always undo a user-initiated ReopenComposite on an all-Cancelled parent.

This makes the spec scenario "Reopening a cancelled parent returns it to Backlog" non-functional in any realistic deployment where child events continue to arrive.

**Required fix.** Pick one semantics, state it in the spec, and reflect it in the design:
- (a) After user-initiated `ReopenComposite`, the parent is treated as user-pinned to Backlog until either the user explicitly starts it or attaches/detaches a child; aggregation is suspended during this window. The recompute handler must distinguish "user reopen" from "child-driven state".
- (b) Reopening the parent is only legal after the user has detached or reopened every Cancelled child; otherwise it is rejected.
- (c) Reopening the parent cascade-reopens the children (contradicts the "no cascade" non-goal, so likely rejected).

Then add a spec scenario ("Reopened parent stays Backlog despite all-Cancelled children until a child state changes / a new child is attached / the parent is re-started") and a design rule (`RecomputeCompositeStatus` takes a flag, or the aggregate carries a "user-reopened, aggregation-suspended" state).

### P3 — "Closing a parent with all children terminal" scenario outcome is non-verifiable (CLARITY)

**Where.** Spec `parent-status-aggregation/spec.md` req 2, scenario 3.

**Problem.** The scenario's THEN is "the parent-aware close guard SHALL accept the operation (subject to the normal 'cannot close Done or archived' rule applied to the parent's current aggregated status)". This is not a verifiable outcome — it says "the new guard accepts, but the old guard may reject", without picking one.

Concretely: if every child is terminal with ≥1 Done, the parent is auto-Done via aggregation (req 1). The existing `Issue.Close()` rejects Done issues (`Issue.Transitions.cs:314`). If every child is Cancelled, the parent is auto-Cancelled, and `Issue.Close()` from Cancelled is effectively a no-op (re-sets Cancelled). So in practice, "all children terminal" means "close is either rejected or a no-op" — the spec should say so.

**Required fix.** Rewrite the scenario with a concrete THEN, e.g. "WHEN all children are terminal, `mo issue close P` is a no-op (P already reflects the aggregated terminal state) and SHALL NOT emit a state-change event" — or split into "all-Done → already Done, close rejected as normal" and "all-Cancelled → already Cancelled, close rejected as normal".

### P4 — Design open question 3 (concurrent last-child detach during recompute) is left as "confirm" but is a correctness concern (CLARITY)

**Where.** Design open questions #3; design D6.

**Problem.** The open question asks whether D6's order (load snapshot → decide → apply → fan-out) is sufficient to handle a last-child detach landing between load and apply, and answers with "*Confirm:* this is sufficient; no explicit lock is needed." This is a real concurrency concern that affects correctness, not a UX preference. Leaving it as "confirm later" pushes a non-trivial correctness argument into the implementer's hands without a decision.

The current argument is also incomplete: if the snapshot is non-empty at load time but empty by apply time (because the child detached between the load and the apply in the *same* synchronous grain call), the transition would throw on the empty-snapshot guard. But within a single grain activation, calls are serialized — there is no "between load and apply" window *inside the same activation*. The hazard only exists across activations, and in that case the next recompute (on `IssueParentChanged`) reconverges. The design should say so explicitly rather than ask.

**Required fix.** Resolve the open question in the design itself: state that within a single grain activation the recompute is atomic by virtue of activation-level serialization, and across activations the `IssueParentChanged` handler always reconverges. Demote to a non-open design note. (This is a clarity fix; not a blocker.)

### P5 — Migration convergence path relies on out-of-scope commands (CLARITY)

**Where.** Design "Migration Plan" step 3; design open question #1.

**Problem.** Step 3 says: "To converge immediately on deploy, run `mo issue list --is-parent` ... and `mo issue recompute <number>` for each." Both commands are then called "optional" and pushed to open question #1, which leans *out of scope*. So the migration's only "immediate convergence" path is unavailable, leaving the workaround: "the existing `--parent none`+`--parent <num>` round-trip on any one child triggers a recompute that converges the parent." That workaround mutates user data (detaches and reattaches a child) just to force a recompute — it is not a safe operational procedure.

**Required fix.** Either (a) put the two commands (or at least `mo issue recompute <number>`) into scope as a small T-005, or (b) rewrite the migration step to say "existing parents converge lazily on the next child event; no immediate-convergence procedure is offered in this change" and accept that parents in the wild may show a stale status until activity resumes.

### P6 — Proposal/design naming inconsistency for parent transitions (MINOR)

**Where.** Proposal "Impact" → "Server — Issue domain"; design D3; tasks T-001/T-002/T-003.

**Problem.** The proposal names the new transitions `MarkParentInProgress`, `MarkParentDone`, `MarkParentCancelled`, `ReopenParent`. The design and tasks rename them to `MarkCompositeStarted`, `MarkCompositeDone`, `MarkCompositeCancelled`, `ReopenComposite`. Either set is fine, but the proposal and the design/tasks should agree so reviewers and implementers don't have to translate.

**Required fix.** Update the proposal's "Impact" section to use the design's names (or vice versa). Trivial text change.

### P7 — `compound-advancement` req 5 (no workflow control surface on parent) lacks a spec scenario for the API route (MINOR)

**Where.** Spec `compound-advancement/spec.md` req 5.

**Problem.** The requirement says "Workflow control operations (retry, rerun, force-stop, resume, approval) SHALL be rejected when invoked on a parent." The existing workflow-control routes (`IssueRoutes.WorkflowControl.cs`) were written assuming every issue has a workflow run; without an explicit scenario and an acceptance criterion in some task, an implementer can easily miss adding the parent-aware rejection (the current path probably throws "no workflow run id" with a generic error, which technically rejects but is not the typed rejection the spec implies).

**Required fix.** Add a spec scenario ("`mo issue retry <parent>` is rejected with a typed error directing the caller to the relevant child") and add a corresponding acceptance criterion to T-002 (or T-003, alongside the other parent-aware rejections).

## Non-blocking observations (not requiring fix)

- The dependency graph in `tasks.json` is a valid DAG; every `dependsOn` points to a strictly lower priority. ✅
- T-002 is large but coherent (mirrors the size of issue #418's T-001, which shipped successfully). Acceptable.
- The `parent` lineage stamp (design D4) correctly mirrors the existing `epic` stamp and is purely additive; no migration needed. ✅
- The `ArchiveForced` escape hatch (design D7) is narrowly scoped and is the simplest way to satisfy the "Cancelled children archive when parent archives" scenario. ✅
- T-004 is independent of T-003, which is correct: the read model projects whatever the aggregate persisted, regardless of lifecycle wiring. ✅
- The "two parents race when a child is moved" risk in the design is somewhat overstated — `AssignParent` already requires the child to be Backlog (from #418), so moving a child never affects either parent's terminal-state math. The handler dispatching recompute to both old and new parent is still correct, just not for the reason stated.

## Verdict

Two blockers (P1 external-prerequisite handler; P2 reopen-vs-re-aggregation) directly threaten the issue's acceptance criteria and must be fixed in the specs and design before T-002/T-003 can be built correctly. P3, P5, and P7 should also be resolved to keep the specs verifiable and the migration operable. P4 and P6 are clarity-only.

<promise>FAIL</promise>
