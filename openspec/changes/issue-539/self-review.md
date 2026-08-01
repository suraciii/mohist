# Self-Review — Issue 539 (WorkflowRun status ETag cache)

Reviewer role: reviewer, not fixer. Findings below; a separate task resolves each.

## Verdict: FAIL

One blocker: the spec and design contradict each other on whether the `State`
column is read on a cache hit, because `GetStatusAsync`'s mandatory definition
resolution already reads `State` twice per call.

---

## Artifacts reviewed

- `openspec/changes/issue-539/proposal.md`
- `openspec/changes/issue-539/specs/workflow-run-status-cache/spec.md`
- `openspec/changes/issue-539/design.md`
- `openspec/changes/issue-539/tasks.json`
- Against issue #539 body and the live codebase.

## Strengths

- Capability boundary is clean: one capability (`workflow-run-status-cache`),
  one spec file, one cohesive task — no over-granular technical-step split.
- D1 (cache the deserialized aggregate only; keep definition + artifact
  resolution per-call) correctly targets the proven LOH source (the full typed
  `JSON.Deserialize<WorkflowRun>`) and avoids inventing artifact/definition
  version signals whose failure mode is *wrong data*.
- D2 (singleton store injected into the scoped querier) is the right lifetime:
  a per-scope cache could not help the cross-request 3s poll. Grounded in the
  codebase's `ISingletonService` convention (`ProjectQuerier` precedent).
- Spec scenarios use exactly 4 hashtags, normative MUST/SHALL language, and map
  1:1 to the proposal's capability. No `ADDED/MODIFIED/REMOVED` headers.
- Migration/rollback are correctly scoped: no schema change, additive code,
  safe revert (ETag column and its increment rule untouched).

---

## Finding 1 — BLOCKER: spec requires "MUST NOT read the State column" on a hit; design (and code) reads it every call

**The contradiction.**

`spec.md` Requirement 1, scenario "First read after process start with a
stable ETag":
> the query MUST serve the cached view and **MUST NOT read or deserialize the
> State column**

and the normative text: "MUST NOT deserialize the run's State", scenario 1:
"the run's State JSON MUST be deserialized zero times during the second call".

But `WorkflowQuerier.GetStatusAsync` calls
`_definitionResolver.LoadTemplateAsync(workflowRunId)` **unconditionally**
(`packages/server/src/Mohist.Server/Workflow/Services/WorkflowQuerier.cs:44`),
and `LoadTemplateAsync` reads `State` on every call:

- `ResolveRunContextAsync` (`WorkflowDefinitionResolver.cs:220-221`):
  `db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(x => x.WorkflowRunId == runId)`
  — loads the **full row, `State` column included**, into the entity.
- `LoadBoundProfileIdAsync` (`WorkflowDefinitionResolver.cs:234-241`):
  `Select(x => x.State)` + `JsonDocument.Parse(state)` — reads `State` again
  and parses it.

The design itself acknowledges this: Context step 3 ("re-runs the definition
cascade … a small `JsonDocument.Parse` over State to read the bound profile
id") and D1 ("definition resolution … remain per-call"). So the design keeps
the `State` read; the spec forbids it.

**Impact.** The primary LOH win (skipping the full typed
`JSON.Deserialize<WorkflowRun>`) is real and consistent across both artifacts.
But the spec's literal guarantee ("MUST NOT read or deserialize the State
column" / "deserialized zero times") is **unachievable** without changing the
definition-resolution coupling. A test written to that scenario cannot pass
against the designed implementation; an implementer following the spec will
either fail the test or be forced to silently deviate from the design. This is
a contract contradiction between two plan artifacts that the build task would
inherit as ambiguity.

**Fix options (for the separate fix task — pick one and reconcile both
artifacts):**

- **(A) Narrow the spec's guarantee** to what the design delivers: on a hit,
  the query MUST NOT perform the full LOH-allocating typed deserialize
  (`JSON.Deserialize<WorkflowRun>`); lightweight metadata extraction (bound
  profile id via `JsonDocument.Parse`) may still read/parse `State`. Update
  Requirement 1 wording + scenarios accordingly. Smallest change; preserves
  the proven win.
- **(B) Extend the design** so a hit avoids the definition-resolution `State`
  re-read: source the bound profile id from the cached `WorkflowRun`
  (`run.WorkflowProfileId`) and source `projectId`/`issueNumber`/`RunExists`
  from scalar projections or the cached aggregate, bypassing
  `LoadTemplateAsync`'s two `State` reads on the hot path. Achieves the spec's
  literal wording; larger refactor of the querier↔definition-resolver coupling.

Either is valid; the plan must not ship with the two artifacts disagreeing.

---

## Finding 2 — Minor: "ETag versions exactly State" overstates precision

`WorkflowRunStore.StageRunAsync` (`WorkflowRunStore.cs:160-166`) unconditionally
re-serializes `State` and increments `ETag` on **every** update save, even when
the serialized content is byte-identical to the prior write. (The
byte-idempotency rule in `design/workflow/run-state.md:64-66` applies only to
cold-start data upgraders, not the runtime store.)

So `ETag` is a save-count proxy, not a content hash: a redundant grain save
invalidates the cache and triggers one redundant deserialize. Correctness is
unaffected (spec's "ETag differs → deserialize once" is honored), but the
design's D3 framing ("ETag is the State version authority and increments on
every actual write") and the spec's "State is unchanged (ETag matches)"
implicitly assume content-equality. Recommend a one-line clarification so the
implementer does not assume content-hash semantics when reasoning about hit
rate.

## Finding 3 — Minor: no-mutation guard is a design risk but absent from task acceptance criteria

Design Risks calls for "a test/arch guard [to] assert no mutation path writes
back to a cached aggregate" (the singleton shares one `WorkflowRun` across
request scopes). That mitigation is **not** reflected in `tasks.json`
acceptance criteria, so the build task has no verifiable gate for it. Either
add an acceptance criterion (e.g. a unit/arch test asserting the cached
aggregate is not mutated by `BuildStatusView`/`AttachArtifactSummariesAsync`/
any per-call path) or drop the risk from the design. As written, a future
caller mutating the shared instance would be a silent concurrency bug with no
guard.

---

## Non-issues (checked, no change needed)

- `EF.Property<long>(e, "ETag")` scalar projection is standard EF Core for
  shadow properties (`ETag` is configured in `MohistDbContext.cs:868` as a
  shadow concurrency token, absent from `WorkflowRunRow`); the design's
  provider-fallback note is sufficient.
- Issue-body reference to "spec: dispatch-snapshot-persistence" points at a
  doc that does not exist; the plan correctly cites `run-state.md` instead.
  Not a plan defect.
- Returning the full cached view (vs. the issue's phrase "轻量摘要") is a
  faithful interpretation — the deserialize-free full view *is* the
  lightweight result.
- `tasks.json` `spec` field omits the `#requirement` anchor; field guidance
  says "when applicable", and one task covers all six requirements, so a
  file-level reference is defensible.

<promise>FAIL</promise>
