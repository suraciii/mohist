# Self-Review: Issue 557 Plan Artifacts (Round 2 — disposition verification)

Reviewer verdict: **PASS**. Round 1 failed the plan on one must-fix (MF-1: Web Agent
list/detail display surfaces absent from the plan, violating AC3). This round verifies
that disposition against the updated `proposal.md`, `design.md`, `tasks.json`, and
`specs/agent-reasoning-effort/spec.md` (`specs/runtime-reasoning-capability/spec.md`
is unchanged since Round 1), re-checks the fix for regressions, and re-scans the delta
for previously-missed must-fix problems. Every codebase claim made by the new text was
verified against the working tree.

## Must-Fix Dispositions

### MF-1. AC3 display coverage gap — FIXED PROPERLY

The fix landed in all four artifacts, matching the remedy Round 1 requested exactly:

- **`specs/agent-reasoning-effort/spec.md`**: new requirement "Web agent surfaces
  display the stored effort" (line 120) with three scenarios — list rows show the
  effort beside model with the variant still its own value; the detail config card
  shows the effort beside Model with the edit-timing note naming the reasoning
  effort among the future-jobs-only keys; absent effort displays as nothing and is
  never synthesized. This closes AC3's "列表、详情…稳定显示 runtime、model、
  reasoning effort；真实 variant 单独显示" on the Web surfaces.
- **`tasks.json` (T-008)**: description extends the shared reader
  (`readAgentModelAndVariant` → also returns `reasoningEffort`) and names
  `AgentListPage` rows and the `AgentDetailPage` Agent Config card with the
  edit-timing note; acceptance criterion 3 requires exactly the spec's behavior
  (effort beside model, variant separate, no value when absent); criterion 7 adds
  Web unit tests for list-row/detail-card display (present, absent, beside a true
  variant) via the extended reader; the notes bind the task to the new requirement.
- **`design.md` (D8/D9)**: D8's Web paragraph specifies the same three surfaces and
  the shared-reader extension; D9 adds the matching Web unit-test line.
- **`proposal.md`**: "What Changes" and the Web Impact bullet now name the Agent
  list rows and detail config card (stored effort beside model, true variant still
  separate) and the shared agent-config reader.

**Codebase grounding verified:** `readAgentModelAndVariant` is the shared reader in
`packages/web/src/entities/agent/api/client.ts:162` (exported via
`entities/agent/index.ts:42`), used by `AgentListPage.tsx:46` whose rows render
model·variant (lines 87–95) and by `AgentDetailPage`'s Agent Config card (Runtime /
Model / Variant rows, testid `agent-detail-runtime`, lines 545–559) with the
edit-timing note at line 567 enumerating "Instructions, Runtime, Model, Variant, and
Skills". Every extension point D8 names exists as described; the fix is buildable as
specified.

## Regression Check on the Fix

Checked, no issue. The delta is confined to the display-coverage additions listed
above (file mtimes confirm `runtime-reasoning-capability/spec.md` untouched):

- The new requirement is internally consistent and consistent with D8: display
  shows the *stored* effort unconditionally while executability is a separate
  readiness concern — no conflict with the editor-control gating requirement
  ("Web exposes effort as its own control").
- The full requirement→task mapping still holds: all nine
  `agent-reasoning-effort` requirements and all five `runtime-reasoning-capability`
  requirements map to tasks (the new requirement maps to T-008 via description,
  AC 3/AC 7, and notes), and every task's spec anchor still resolves to an existing
  requirement header (T-008's `#web-exposes-effort-as-its-own-control` matches).
- T-008's scope grew (display + docs + final `npm run verify`) but remains the
  terminal task with nothing depending on it; its `dependsOn` (T-001, T-003, T-007)
  still yields a sound order and covers everything it consumes.
- No other artifacts were disturbed: D1–D7, the decision table, the fence design,
  the migration plan, and T-001–T-007 acceptance criteria are as swept in Round 1
  (whose per-dimension verdicts — coverage, correctness, codebase consistency, task
  breakdown — I re-confirmed on the unchanged portions).

## Previously-Missed Problems

None found in the delta at the must-fix bar. The added text's claims about the Web
codebase are accurate (verified above), and the addition creates no gap,
contradiction, or untestable criterion. Nothing else in the delta could hide a
must-fix: it is purely additive display coverage.

## Observations (do not affect the verdict)

Carried from Round 1 — none were addressed, and none were required to be (each was
explicitly optional; their justifications still hold):

1. **Pinned-runner / workspace-home admission explicitness** (unchanged): D5 names
   `DispatchService.AddPendingDispatchesAsync` as the resolver site and the fence
   requirement is unconditional over all claims, so the mechanism covers
   pinned/home-elected work; but D5/T-006 still do not name those election paths,
   and T-006's test criteria omit pinned/home scenarios (incompatible tuple on a
   pinned runner; `capabilityRevision` sourcing when the pinned runner's catalog is
   incomplete at election time). Recommend one design note + tests in T-006.
2. **AC6 wording tension** (unchanged): absent evidence → wait vs explicit negative
   evidence → terminal failure is the coherent, already-accepted split; the design
   still lacks the one-sentence acknowledgment of the literal AC6 reading. Fine to
   keep.
3. **`EventCatalog` terminology** (unchanged): "registered as EventCatalog failure
   categories" (proposal, T-006) is loose — failure categories live elsewhere; the
   implementer should place them where failure categories actually live.
4. **Spec anchor nit** (unchanged): T-001's `#write-surfaces-accept-reasoneffort`
   should be `#write-surfaces-accept-reasoningeffort`.

New this round:

5. **T-008's `spec` field anchors only the editor-control requirement**; the new
   display requirement is satisfied via the description, AC 3/AC 7, and notes
   rather than a second anchor. Cosmetic (the `spec` field is single-valued and the
   notes name the requirement verbatim) — no action required.
6. **CLI `mo agent list`** still renders no runtime/model/variant columns today, so
   extending it remains optional scope; the Web surfaces were the AC3 gap and are
   now covered.

## Summary

MF-1 is fixed properly: a spec requirement with three scenarios, T-008 acceptance
criteria with tests, design D8/D9 text, and proposal coverage — all grounded in the
verified shared-reader/list/detail code. The fix introduced no regressions, the
requirement→task mapping is complete, and no previously-missed must-fix problem
exists in the delta. The plan is ready to build.

<promise>PASS</promise>
