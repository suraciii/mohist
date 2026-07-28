## Context

Issue #511 bundles a batch of server-side debt whose common trait is "the fix is unique and needs no design judgment." The proposal (`openspec/changes/issue-511/proposal.md`) establishes the *why*; the specs (`openspec/changes/issue-511/specs/`) state the *what*. This document covers the *how*.

Relevant current state, verified against the code:

- **Dead dispatch path.** `IWorkflowGrainContext.DispatchEvent` (`IWorkflowGrainContext.cs:22`) is implemented in `WorkflowGrain.cs:90` as `=> On(e)`. `On` (`WorkflowGrain.cs:644-667`) is a 19-arm switch where every arm returns `Task.CompletedTask`. `WorkflowWorkLifecycle.cs:128` loops events into it. `CommitAsync(events, reason, ct)` threads `reason` only to feed `On`.
- **Production backdoor.** `WorkflowGrain.BindProfileForTest` (`WorkflowGrain.cs:60`) is a settable `Func<string,string,Task<WorkflowProfileReferenceResult>>?`. Production binding otherwise goes through `GrainFactory.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId).BindWorkflowRunAsync(...)` (`WorkflowGrain.cs:680-685`). One consumer: `WorkflowGrainStateSaveFailureSpecs.cs:188`, which constructs the grain **manually** (not via the test cluster) using `GrainTestContext.Create` — a `DispatchProxy` runtime whose `GrainFactory` returns `null`. The existing coordinator-bypass ArchTest (`ArchitectureRules.cs:441 BindingParticipantInterfaces_OnlyConsumedByCoordinator`) is type-based and cannot see this delegate seam.
- **Message-text control flow.** `WorkflowGrain.CommitAsync` (`WorkflowGrain.cs:624-626`) catches `InvalidOperationException` and branches on `ex.Message.Contains("no current definition") || ex.Message.Contains("no definition for stage")`. Those messages originate in `WorkflowProfileManager.cs:71,154,440`.
- **Misnomer.** `WorkflowRunProfileManager` (`WorkflowRunProfileManager.cs`, 212 lines) reads/writes Run-scoped Variables, backed by `WorkflowRunProfileRow` / the `WorkflowRunProfiles` DbSet. `design/conventions.md:35` reserves the `Store` suffix for "persistence boundary for one shape."
- **Status word table.** `WorkflowStatusMapper.FrontendStatus(string raw)` (`WorkflowStatusMapper.cs:10-15`) special-cases `"AwaitingApproval"` and `ToLowerInvariant()`s everything else. It is fed `.ToString()` from four enums: `WorkflowRunStatus`, `StageRunStatus`, `TaskRunStatus`, `StageCheckStatus` (the issue body's `CheckRunStatus` is a misnomer for the real `StageCheckStatus`).
- **Comment rot.** 38 offending comment references across 26 files in `src/`; one migration comment cites the non-existent `design/workflow/scheduling.md`; `UnifiedSessionRoutes.cs:22` cites `T-005`. CLI still consumes `agent-sessions/{sessionId}` (`packages/cli/tests/.../CliSessionCommandSpecs.cs`), so the T-005 remark is a live fact, not stale.
- **ArchTests plumbing (important enabler).** `Mohist.Server.ArchTests.csproj` **already embeds production source** as `ServerSources/**/*.cs` resources, test source as `TestSources/...`, and `spec-file-size-baseline.json` as `SpecFileSizeBaseline.json`. `Microsoft.CodeAnalysis.CSharp` is already referenced. So a comment-scanning ArchTest has free access to production C# text and a Roslyn pipeline.

Constraints: external behavior, the literal wire-format status values, and the profile-resolution-failure user message all stay byte-identical. No new external dependencies. Verification is the full server suite (unit + spec + ArchTests) and web typecheck + test.

## Goals / Non-Goals

**Goals:**
- Land all five groups (A–E) of mechanical debt in independently reviewable commits.
- Make the two recurrence-prone items (status word table, comment references) fail-fast at build time via gatekeeper tests.
- Close the production coordinator-bypass hole so the existing ArchTest's promise becomes literally true.

**Non-Goals** (carry forward from issue):
- OpenAPI codegen to sync the four web unions (disproportionate effort).
- Splitting `WorkflowProfileManager` responsibilities (separate issue).
- Teardown of test-only methods on *other* grains (separate issue).
- Comment cleanup in runner / web / cli (server only here).
- Adding or removing status enum values or `WorkflowEvent` types.

## Decisions

### D1 — Group A: remove the dead path, close the backdoor, type the exception

**Dead path.** Delete `IWorkflowGrainContext.DispatchEvent`, the `WorkflowGrain.On` method, the `WorkflowWorkLifecycle.cs:128` loop, and the `reason` parameter from `CommitAsync`. Verify `reason` has no other consumer (it does not — only `On` reads it). No replacement: the path produced nothing.

**Exception typing.** Introduce a typed exception for profile/definition resolution failure. The minimal, self-describing shape: a single `WorkflowDefinitionResolutionException` carrying a typed `Reason` discriminator (`NoCurrentDefinition`, `NoStageDefinition`, …) thrown by `WorkflowProfileManager` at the three sites (`:71`, `:154`, `:440`). `CommitAsync` catches **the type** and switches on `Reason` if it needs to distinguish; today both reasons trigger the same `FailDefinitionResolution(ex.Message)` path, so a single catch suffices.
- *Alternative considered:* separate exception subtypes per reason (`NoCurrentDefinitionException`, …). Rejected — heavier than the one branch needs; one type + discriminator is enough and keeps the throw sites cheap to read.

**Backdoor removal — chosen approach.** Replace the settable `BindProfileForTest` delegate with **no production field at all**; the binding call stays as the inline `GrainFactory.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(...)` call. The one manual-grain test switches to a **fake `IGrainFactory`** that returns a stub coordinator, wired through an extended `GrainTestContext`.
- Concretely: extend `GrainTestContext.GrainRuntimeProxy` (and/or `GrainContextProxy`) to return a configurable `IGrainFactory` for `get_GrainFactory` instead of `null`. The fake factory (modeled on the existing `RegistryGrainFactory` in `RunnerRegistryCatalogSourceTests.cs:179`) returns a stub `IWorkflowProfileReferenceCoordinatorGrain` whose `BindWorkflowRunAsync` yields the `Applied` result the test needs.
- *Alternative A (convert the spec to the `InProcessTestCluster` + register a fake coordinator grain):* most faithful to the issue's "register a fake coordinator grain in the test cluster" wording. Rejected as the primary path because `WorkflowGrainStateSaveFailureSpecs` deliberately constructs the grain by hand to inject a controllable failing `IWorkflowRunStore`; routing it through the cluster fights that intent and bloats the fixture. The cluster approach remains the fallback if the proxy-factory route turns out fragile.
- *Alternative B (constructor-injected `IWorkflowProfileBinder` strategy):* clean DI seam, production default = call coordinator. Rejected because it introduces a new abstraction purely for one test and overlaps the explicitly-deferred "teardown of test-only methods on other grains" non-goal. Keep the model minimal.

The existing type-based coordinator ArchTest needs **no change** — once the field is gone, there is nothing for it to miss. We add one assertion-scenario spec proving no settable binding delegate exists on `WorkflowGrain`.

### D2 — Group B: rename to `…VariablesStore`; table decision deferred to a recorded choice

**Rename.** `WorkflowRunProfileManager` → `WorkflowRunVariablesStore`, repo-wide (14 files confirmed). DI registration is implicit via `IScopedService` marker — no hand-rolled `Add...` call to update. Methods drop `Profile` wording only where it carries no meaning; signatures and `VariableBundle` JSON shape are untouched.

**Table — chosen approach: keep `WorkflowRunProfiles` / `WorkflowRunProfileRow` under a documented decision.** Renaming an EF Core table + row for cosmetic correctness costs a migration that touches persisted production rows, plus coordinated down/up scripts, for zero behavioral gain. The cost/benefit is unfavorable, and the AGENTS.md rule is "不得默默留着" (must not be left *silently*) — a recorded decision satisfies it. So: add a short note in `design/` (e.g. appended to the relevant workflow-data design file) stating `WorkflowRunProfiles`/`WorkflowRunProfileRow` is a deliberately-retained historical misnomer, with a one-line `// historical misnomer: stores Run-scoped Variables; see design/...` pointer on the row type so the next reader is not misled.
- *Alternative considered:* full rename + EF Core migration (`RenameTable`, update `DbSet`, `ToTable`, row type name). Rejected for cost/benefit, but documented here so the choice is auditable; revisit if the table is ever restructured for a real reason.

### D3 — Group C: typed per-enum wire mapping; `FrontendStatus` → `WireStatus`; align web unions

**Mapping.** Replace `FrontendStatus(string)` with four typed entries on `WorkflowStatusMapper`, each a switch expression over its enum with **no discard arm**:
- `WireStatus(WorkflowRunStatus)`, `WireStatus(StageRunStatus)`, `WireStatus(TaskRunStatus)`, `WireStatus(StageCheckStatus)`.
- Each arm emits the exact current wire value (`AwaitingApproval → "awaiting-approval"`; single-word values → lowercase). A shared private `Kebab(string)` helper may convert PascalCase→kebab-case, but **only inside an explicit arm**, never as a catch-all fallback — exhaustiveness comes from listing every enum value.
- Exhaustiveness is enforced structurally: a new enum value with no arm is a compile error (C# switch expressions are non-exhaustive on enums by default, so the gatekeeper is a per-enum spec test using `Enum.GetValues`, plus optionally a `_ => throw new SwitchExpressionException(...)` arm to make runtime omissions loud rather than silent). The spec test is the hard guarantee; the throw arm is defense-in-depth.

**Rename.** `FrontendStatus` → `WireStatus` and the test file/class `WorkflowStatusMapperFrontendStatusTests` → `…WireStatusTests`. Call sites in `WorkflowStatusMapper.cs` (4) and the test file update mechanically. No external (API/CLI) caller names the symbol.

**Web unions.** `packages/web/src/entities/issue/model/{workflow-run,stage-state,recovery}.ts`: add a JSDoc/comment above each of the four unions naming its authoritative server enum (`WorkflowRunStatus`, `StageRunStatus`/`StageCheckStatus` for the stage unions, `WorkflowRunStatus` for recovery summary). Do **not** prune client-only states (`skipped`, `error`) — the spec only requires the union *include* every server wire value and name the source of truth. Verified by typecheck + existing tests.

### D4 — Group D: Roslyn-based comment ArchTest with a baseline ratchet

**Scanning.** Because the ban is on *comments* (string literals may legitimately contain `design/foo.md`), scan comment trivia, not whole files. `Microsoft.CodeAnalysis.CSharp` is already referenced and production source is already embedded as `ServerSources/**/*.cs`. The test parses each embedded source with `CSharpSyntaxTree.ParseText`, walks `SyntaxTrivia` of kind `SingleLineComment`/`MultiLineComment`/`SingleLineDocumentationComment`/`MultiLineDocumentationComment`, and regex-matches `issue-\d+ | T-\d{3} | design/[^*\s]+\.md | openspec/` (anchored, `RegexOptions.ExplicitCapture`).
- *Alternative considered:* regex over raw file text. Rejected — would flag legitimate string literals and XML doc attributes, producing false positives that erode trust in the gate.

**Ratchet.** New `comment-reference-baseline.json` embedded as `CommentReferenceBaseline.json`, mirroring `SpecFileSizeBaseline.json`. Format: `{ "<relative path under ServerSources>": <occurrence count> }`. The test:
1. Counts current offenders per file.
2. For each file: current count MUST be ≤ baseline count (shrink-only). Overshoot → fail with the offending file + snippets.
3. Baseline entries whose file now has zero offenders MUST be removed from the JSON in the same commit (the test fails if a baseline entry is stale, exactly like the size baseline's "must be removed" rule).
- *Alternative considered:* a baseline of exact line snapshots rather than counts. Rejected — line snapshots are brittle under unrelated edits; counts ratchet monotonically and are cheap to maintain.

**Hard ban.** Once the 38 offenders are cleared (Group D cleanup), empty the JSON. The test then has an empty baseline and fails on any offender — the no-exemption end state.

**Targeted cleanups (folded into clearing the baseline):**
- `20260706120000_AddWorkflowRunReadySince.cs`: the citation to `design/workflow/scheduling.md` is removed; the fairness/VIRTUAL schema decision it pointed at is written inline (or the comment deleted if purely citational).
- `UnifiedSessionRoutes.cs`: remove `T-005`; verify (done — CLI uses `agent-sessions/{sessionId}`) and restate the old-route coexistence as a plain fact. If, by implementation time, the CLI has migrated off the old route, delete the remark instead.
- The remaining 36: pure provenance tags (`(issue-490 T-001, design D2)`) are deleted, preserving surrounding explanatory prose; "why" comments are rewritten to state the reason without the citation.

### D5 — Group E: micro-cleanups (with their test rewrites)

- `EventDispatcherService.Backoff`: make `private`. The current direct unit assertions (`EventDispatcherSpecs.cs:876-879`) are rewritten to observe retry cadence through `DispatchAsync` advanced by `FakeTimeProvider` — i.e. assert `NextAttemptTime` advances by the expected backoff, not by calling `Backoff` directly. This is a real test rewrite, not a visibility tweak.
- `WorkflowProfileManager.ResolveLayeredVariablesAsync`: delete the pass-through wrapper; inline `ResolveConfiguredVariablesAsync(runId)` at the one production call site (`:363`). The 7 spec call sites in `WorkflowVariableResolutionSpecs.cs` / `WorkflowVariableResolutionDefaultsSpecs.cs` switch to `ResolveConfiguredVariablesAsync` (same return shape).

## Risks / Trade-offs

- **[Manual-grain test becomes proxy-factory-dependent]** -> If extending `GrainRuntimeProxy` to return a fake `IGrainFactory` proves fragile across other manually-constructed grains, fall back to Alternative A (run the spec through `InProcessTestCluster` with a registered fake coordinator). Decide at implementation time; the spec contract is unaffected either way.
- **[Exhaustiveness relies on a spec test, not the compiler]** -> C# switch expressions over enums are *not* exhaustive-checked by default. Mitigation: the per-enum `Enum.GetValues` spec test is the hard guarantee, plus a `_ => throw new SwitchExpressionException` arm so a runtime miss is loud. Document this in the mapper so no one deletes the throw arm.
- **[Table misnomer left in place]** -> A future reader could re-introduce Profile-flavored logic against the table. Mitigation: the inline comment pointer on `WorkflowRunProfileRow` + the `design/` note make the decision discoverable; the rename is revisited whenever the table is restructured for a real reason.
- **[Comment baseline maintenance burden]** -> A ratchet that must be hand-edited can drift. Mitigation: mirror the proven `spec-file-size-baseline.json` mechanics (stale-entry detection forces removal); the baseline is empty after this issue, so ongoing burden is zero.
- **[Backoff test rewrite changes what is asserted]** -> Moving from direct `Backoff(n)` asserts to timing-via-`FakeTimeProvider` is a behaviorally weaker-but-more-honest assertion. Mitigation: assert the *sequence* of `NextAttemptTime` deltas for several attempts, not just one, so the exponential curve is still pinned.
- **[Rename span is wide]** -> 14 files / 32 refs for the VariablesStore rename alone. Mitigation: land Group B as its own commit so a revert is a single `git revert`.

## Migration Plan

This change is internal; no user-facing migration. Deployment is a normal server deploy. Per-group commit ordering (each independently reviewable and revertible):

1. **Group A** — dead-path removal + typed exception + backdoor removal (with the test rewire). Smallest blast radius for the grain; do first so the coordinator ArchTest's promise holds for the rest.
2. **Group B** — `WorkflowRunVariablesStore` rename + the table-decision note. Pure rename commit; trivial to review by diff.
3. **Group C** — typed wire mapping + `WireStatus` rename + web union comments.
4. **Group D** — add the ArchTest **with the frozen 38-entry baseline first** (proves the ratchet works), then a follow-up commit that clears the offenders and empties the baseline. Two commits so the ratchet's "shrink-only" transition is visible.
5. **Group E** — `Backoff` private + test rewrite, and `ResolveLayeredVariablesAsync` inline.

**Rollback:** each group is a standalone commit; `git revert <sha>` per group is the rollback unit. No schema change ships (table rename decision is *not* taken), so no DB rollback is needed. The new ArchTest + baseline JSON ride with Group D; reverting Group D restores the pre-ban state cleanly.

**Verification gate per group:** `dotnet build Mohist.sln` + the relevant test subset; the final group also runs the full server suite and web `typecheck` + `test`.

## Open Questions

- **Backdoor replacement wiring:** confirm at implementation time whether the `GrainRuntimeProxy` fake-factory extension (D1 primary) is clean enough, or whether the cluster + registered-fake-coordinator route (D1 Alternative A) is lower-risk for this one spec. The spec contract is identical either way; only the test mechanics differ.
- **`UnifiedSessionRoutes` remark at implementation time:** the CLI currently uses `agent-sessions/{sessionId}` (verified), so the remark becomes a fact. If a parallel CLI change migrates off the old route before this lands, the remark is deleted instead — re-check at implementation time.
- **Comment baseline granularity:** counts-per-file is the default; if two offenders sit on adjacent lines in one file and only one is removed, a count-based baseline still tightens correctly. Confirm the chosen JSON shape (`path → count`) is ergonomic for reviewers during the clearance commit.
