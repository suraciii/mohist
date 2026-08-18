# Review — Issue 557: reasoningEffort as first-class execution configuration

Re-review of the previous FAIL verdict. The previous round's only must-fix finding
(MF-1: OpenCode agent execution silently disabled in production because the shipped
runner registered the OpenCode catalog permanently non-authoritative) was the basis
for FAIL. Three follow-up commits — `5cfed349e fix: publish authoritative OpenCode
catalog and honor empty/no-discovery model lists`, `cb1e6ea73 test: cover production
OpenCode admission composition`, `a4c03ffc9 docs: record MF-1 OpenCode admission fix
in progress notes` — were made before this re-review. No other change in the change
set was reverted or mutated.

## Verdict

**PASS** — the must-fix finding is properly disposed and the change is ready to
merge.

## Disposition of the previous must-fix finding

### MF-1 — OpenCode agent execution is silently disabled → FIXED

**The fix.** Two coordinated changes remove the production admission hang:

1. `packages/runner/src/runtime/host.ts:822-845` now publishes the OpenCode catalog
   as authoritative: `complete: true` and a content-derived
   `capabilityRevision: deriveCapabilityRevision(opencodeEntry)`. The catalog still
   reports the runtime's explicit effort contract
   (`supportsReasoningEffort: false`), so an explicit effort produces a
   deterministic `unsupported_execution_configuration` failure before any dispatch
   reaches the runner.
2. `packages/server/src/Mohist.Server/Agent/Services/AgentExecutionCapabilityResolver.cs:151-204`
   treats empty `Models`, `ReasoningEfforts`, and `Variants` as the "no discovery
   yet" signal. Model, effort-membership, and variant-membership are enforced only
   when the catalog has explicit content; an empty list means the runtime validates
   that field at execution time. The `SupportsReasoningEffort != true` flag is still
   the source of truth for effort support, so an effort on OpenCode
   (`supportsReasoningEffort: false`) is still deterministically rejected.

Both changes match the design contract in `design/runtimes/opencode.md:484-491`:
the catalog "assists configuration; it is not execution or readiness authority" and
"OpenCode still validates the selected model and variant when execution starts"
while "an explicit effort remains an execution-configuration failure."

**Why it is fixed, not patched around.**

- The composition bug (catalog non-authoritative *and* dispatch fence requiring
  an authoritative catalog) is resolved on both sides: the catalog becomes
  authoritative, and the fence actually permits empty-list configurations instead
  of holding them at `needs-setup`.
- An explicit effort on OpenCode still resolves to `unsupported_execution_configuration`
  with a frozen tuple, capability revision, and recorded failure evidence
  (DispatchService.cs:316-336, `Reject` branch in the resolver). The
  `ProductionOpenCodeShape_RejectsExplicitEffortDeterministically` SpecTest asserts
  every one of those evidence fields end-to-end.
- The runner's previous `runner-host-lifecycle.spec.ts` and
  `runner-host-opencode-runtime.spec.ts` assertions of the exact non-authoritative
  OpenCode shape are updated to the new authoritative shape, in three places
  each. The lifecycle suite (lifecycle + OpenCode-runtime specs) runs green:
  26/26.

**Tests added to lock the composition down.**

- `AgentExecutionCapabilityResolverTests.cs` (seven new unit tests, all run green
  as part of the 2736 Server UnitTests):
  - `UnsetModelOnCompleteCatalog_IsSupported`
  - `UnsetModelOnEffortUnsupportedRuntime_IsSupported`
  - `OpenCodeProductionShape_AcceptsAnyModelWithoutDiscovery`
  - `OpenCodeProductionShape_RejectsExplicitEffortDeterministically`
  - `OpenCodeProductionShape_RejectsExplicitEffortEvenWithoutModel`
  - `OpenCodeProductionShape_AcceptsExplicitVariant`
  - `AuthoritativeCatalogWithExplicitModelRejectsUnknownModel`
- `AgentJobCapabilityFenceSpecs.cs` (two new SpecTests, registered runner with
  the exact production OpenCode shape — `complete: true`, content-derived
  `capabilityRevision`, empty `Models`/`Variants`,
  `supportsReasoningEffort: false`):
  - `ProductionOpenCodeShape_AdmitsModelBearingJobWithoutDiscovery` proves the
    end-to-end dispatch is granted and the job reaches `Running` with the
    expected capability revision. This is the test the previous round noted was
    missing: it uses `RuntimeCatalogs` on `RunnerInfo` and exercises the
    `DispatchService` admission path, not an injected `CapabilityCatalogTestHelpers`
    catalog. The previous-round "suite cannot see the hang" critique is closed.
  - `ProductionOpenCodeShape_RejectsExplicitEffortDeterministically` proves the
    `unsupported_execution_configuration` preflight failure path end-to-end,
    asserting the frozen tuple and capability revision in the failure message.

**Test-run evidence collected in this re-review.**

- Server UnitTests: 2736 passed, 0 failed.
- Server SpecTests: 3948 passed, 0 failed (ran the whole suite, not just the
  new tests; the targeted `--filter-method "*AgentJobCapabilityFenceSpecs*"`
  also shows 8/8 green).
- Server ArchTests: 68/68 green.
- Runner vitest: 1668 passed (155 files).

The MF-1 finding no longer holds.

## Regression check

The resolver's permissiveness for empty-list catalogs is a deliberate, behavior-
bounded change. I traced the existing `AgentExecutionCapabilityResolverTests`
(which use `CompleteCatalogEntry()` with populated `Models`/`Variants`/
`ReasoningEfforts`) and the existing `AgentJobCapabilityFenceSpecs` against the
new resolver line by line. Every test that asserts a specific disposition
against a populated catalog still reaches the same disposition (the
`Models.Length > 0` and per-model dictionary checks short-circuit identically
to the prior code path). The new permissive branches only activate on
`Models.Length == 0` / `Variants.Count == 0` / `ReasoningEfforts.Count == 0`,
which the prior complete-catalog tests never hit.

I paid attention to the one shape that *does* exercise the new branches in the
existing suite: `SavedPiThinkingLevelVariant_FailsPreflightWithoutMigration`
(AgentJobCapabilityFenceSpecs.cs:115-170) registers Pi with an explicit
`Variants` map (`model-a: [balanced]`) and submits a job with `variant=high`.
The new resolver still rejects this with `incompatible_execution_configuration`
because the map is non-empty. The SpecTest passes (I observed it in the
3948-pass run), confirming the explicit-catalog branch is not weakened.

No regression meets the must-fix bar.

## Pre-existing problems missed in earlier rounds

None. The previous review's per-dimension sweeps already covered coverage,
correctness, consistency, and tests; the only must-fix returned was MF-1, which
is now resolved. Nothing the per-dimension sweeps missed in earlier rounds has
become must-fix in the current code, because the only code change in scope is
the MF-1 fix and its tests/docs.

## Per-dimension verdicts (re-verified, abbreviated)

- **Coverage** — checked, the only outstanding acceptance gap (MF-1) is closed;
  every other criterion still has its implementation and tests.
- **Correctness** — checked, the new behavior matches the OpenCode design
  contract (configuration hint, not execution authority; effort opt-out stays
  deterministic) and prior pinned-must-fix correctness holds.
- **Consistency** — checked, the new content-derived `capabilityRevision`
  matches the same canonicalization pattern the Pi entry already uses; the
  resolver's comments cite `design/runtimes/opencode.md`, and the
  `comment-reference-baseline.json` was extended in step with the new citation.
- **Tests** — checked, the previously identified gap ("suite cannot see the
  hang" because every admission test either injected authoritative catalogs or
  omitted them entirely) is closed by the two new SpecTests that register the
  exact production shape.

## Observations (do not affect the verdict)

Carried verbatim from the previous review except for #1, which is no longer
accurate.

1. (Dropped) The previous "progress.txt claim that the non-authoritative
   OpenCode entry preserves current behavior" was inaccurate for model-bearing
   OpenCode jobs; the `## MF-1 (review-disposed)` block added in `a4c03ffc9`
   now describes the production shape accurately.
2. Web editor: when the selected model's catalog no longer lists a stored
   canonical effort, the select displays "No reasoning effort" and saving
   silently stores `null`, dropping a still-valid canonical value. Out of scope
   for this issue.
3. `normalizeReasoningEffort` (pi/runtime.ts) treats a whitespace-only string as
   non-empty and fails the turn with `invalid-input` instead of treating it as
   unset. All write surfaces reject whitespace-only efforts earlier, so this is
   unreachable today. Out of scope.
4. Runner-side `staleCapabilityResult` purposely ignores nested
   `with.options.reasoningEffort` (non-Agent actions freeze effort unset;
   server-side fence covers template-preserving `${{ vars.agent }}` dispatches
   via the `Variables` payload). A one-line code comment would help future
   readers; out of scope.
5. `mo agent list` still renders no runtime/model/variant/effort columns; out of
   scope (criteria three's "list" is satisfied by exposing the data through the
   API, which the earlier round's coverage sweep already verified).
6. Plan-level observations carried from `self-review.md` (pinned-runner
   admission explicitness, AC6 wording tension, EventCatalog terminology,
   T-001 spec-anchor spelling) were not addressed; none is required by the
   acceptance criteria.

<promise>PASS</promise>
