# Review — Issue 557: reasoningEffort as first-class execution configuration

Scope: the full change on `mohist/run-wr_c51d35390b694de49df5f76f6f9e05f1` (41 commits
vs. merge-base `7f712e54`), judged against the two capability specs
(`agent-reasoning-effort`, `runtime-reasoning-capability`) and the issue proposal. This is a
first review (no prior `review.md` existed). Server UnitTests re-run during this review:
2729 passed. I did not re-run the full SpecTests/Runner/Web suites; I verified their claims
by reading the new specs and code paths and by spot-checking the loop points.

## Verdict

**FAIL** — one must-fix finding: the admission fence silently disables OpenCode (the
default agent runtime) execution in production. Details first, then the per-dimension
sweep.

## Must-Fix Findings

### MF-1. OpenCode agent execution is silently disabled: model-bearing OpenCode tuples are held Pending forever

**What is wrong.** Previously, OpenCode agent jobs (and `mohist/opencode` workflow tasks
with a model/variant option) were claimed and executed normally: `DispatchService`
called `TryClaimAgentJobAsync`/`TryClaimWorkflowAsync` without a capability fence, and the
runner executed them through the OpenCode adapter. After this change, every OpenCode tuple
that names a model, effort, or variant can never be admitted:

1. The shipped runner registers the OpenCode catalog **permanently non-authoritative**:
   `packages/runner/src/runtime/host.ts:822-830` publishes
   `opencode: { models: [], variants: {}, supportsReasoningEffort: false }` with **no**
   `complete` and **no** `capabilityRevision` (only the Pi entry gets `complete: true` +
   a derived revision, lines 832-842). The runner has no OpenCode model discovery, so this
   entry can never become authoritative.
2. Admission requires an authoritative catalog: `DispatchService.ResolveExpectationAsync`
   (packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:292) → pure
   resolver `AgentExecutionCapabilityResolver.Resolve` →
   `IsAuthoritative` (packages/server/src/Mohist.Server/Agent/Services/AgentExecutionCapabilityResolver.cs:151,
   requires `Complete == true` + non-blank `CapabilityRevision`) → `authoritative.Length == 0`
   → `needs-setup` (line 107-108).
3. `ResolveExpectationAsync` returns `null` for a pending disposition, and
   `AddPendingDispatchesAsync` then executes `if (expectation is null && RequiresCapabilityFence(tuple)) continue;`
   (DispatchService.cs:241 and :269). `RequiresCapabilityFence` (line 363) is true for any
   tuple with a non-empty model/effort/variant — i.e., essentially every useful OpenCode agent.

Result: a user who launches an OpenCode agent (the **default** runtime when none is set —
`AgentLauncher.ResolveRuntime`, and the exact command documented in `docs/agent-sessions.md`
example `mo agent create ... --runtime opencode`) gets a job that sits in `Pending`
indefinitely: no dispatch, no terminal failure, no error, no guidance. Workflow
`mohist/opencode` and `mohist/agent`-resolving-to-opencode tasks with a model are affected
the same way via the workflow branch (DispatchService.cs:260-269).

**Acceptance criteria violated.**

- Proposal, "Translate canonical effort only inside the selected runtime adapter": OpenCode
  "reports `supportsReasoningEffort=false` today, so an explicit effort on OpenCode is an
  explicit configuration failure — never a silent drop." With this implementation an OpenCode
  job with an effort never resolves to `unsupported_execution_configuration` (the
  non-authoritative entry can never trigger that disposition) and never reaches the adapter;
  it silently hangs. The runtime spec scenario "OpenCode rejects an explicit effort" (GIVEN
  an OpenCode dispatch carrying an explicit reasoning effort, THEN the executor fails with
  `unsupported_execution_configuration`) is unreachable end-to-end because no OpenCode
  dispatch is ever admitted.
- The runtime spec's OpenCode registration scenario ("reports `supportsReasoningEffort=false`
  with its true variants preserved") is met literally, but the surrounding acceptance
  contract treats OpenCode as a functioning runtime; hanging it is a pre-existing-feature
  regression the issue does not authorize (the issue's out-of-scope list covers only *native
  OpenCode effort support*, not OpenCode execution itself).

**Why the suite does not catch it.** The spec test that encodes this
(`MissingCatalog_LeavesAssignedJobPendingWithoutDispatch`,
packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Grain/AgentJobCapabilityFenceSpecs.cs:19)
registers a runner with **no** runtime catalogs at all, which is not the production shape:
production always registers the non-authoritative OpenCode entry. Every other admission test
injects an authoritative catalog (`CapabilityCatalogTestHelpers.Create()` has
`Complete: true` for both runtimes), so no test exercises a model-bearing OpenCode job
against the real runner's registration and asserts an outcome. The runner's own
`runner-host-lifecycle.spec.ts:253` asserts the exact non-authoritative OpenCode shape
without connecting it to dispatch admission, so both sides of the system are tested in
isolation and the composition is untested.

**Where to fix (for the disposal task).** Reconcile the runner's OpenCode registration with
the admission contract. Per the design's own intent (`design/runtimes/opencode.md`:
"RunnerHost, not OpenCodeRuntime, discovers it from the operator-provided CLI"),
`host.ts` should publish an authoritative OpenCode entry (from whatever model/variant
discovery exists — a curated/configured model list, or an expression of "any model is
supported"), so supported OpenCode tuples resolve `supported` and effort-carrying tuples
resolve `unsupported_execution_configuration` deterministically. If no OpenCode discovery
source exists at all, either (a) stop advertising OpenCode as a launchable agent runtime
and reject its use explicitly with guidance, or (b) add a first-class admission carve-out
for runtimes whose catalog is non-authoritative *solely because they predate catalog
support* — never a silent indefinite wait. Adding a spec test that registers the exact
production OpenCode shape and asserts the chosen outcome is mandatory.

## First-review sweep: per-dimension verdicts

### Coverage — checked, one acceptance gap (the MF-1 OpenCode path)
Every other acceptance criterion has an implementation and a test:

- Canonical vocabulary (`off`…`max`, string-or-null, unset): `AgentConfigSchema`
  (`CanonicalReasoningEffortsOrdered`, `ValidateReasoningEffort`) on both Agent-definition
  and Issue surfaces; non-canonical values rejected with the named set; effort/variant
  independence holds (no field is derived from the other).
- Write surfaces: server API, CLI `--reasoning-effort`/`--clear-reasoning-effort` (mutually
  exclusive, local pre-request validation; update merge keeps current config; `mo agent view`
  renders the effort), Web editor control, issue-level override (key in `IssueAllowedKeys`,
  forwarded into `vars.agent` via `Filter`; `runtime` still rejected on the issue surface).
- Snapshot freezing: `AgentExecutionDefinition` (Id 6), `AgentJobInput` (Id 25),
  `RoutedAgentLaunchPlan` (Id 22), manual-launch envelopes, dispatch `with`, runner
  session-target/follow-up/turn options — all append-only, absent-effort stays null;
  `EquivalentInput`/`EquivalentCommand` include effort so replays cannot rewrite a frozen
  snapshot.
- Catalogue wire: append-only IDs 2-5, Pi publishes thinking levels only under
  `reasoningEfforts` with empty `variants`, `deriveCapabilityRevision` canonicalizes before
  hashing, legacy entries deserialize null and stay non-authoritative.
- Readiness: frozen-effort comparison in definition matching, `effort-without-model` gap,
  both execution-configuration categories → Needs setup with `reasoningEffort` guidance.
- Evidence: `AgentJobTerminalResult` gains Model/Variant/ReasoningEffort; session model
  facts carry adapter-reported `appliedReasoningEffort`, cleared when the latest fact omits
  it; no default synthesized.
- Pi smuggling removal: `setThinkingLevel` is now reachable only from
  `mapThinkingLevel(normalizeReasoningEffort(...))`; the variant never reaches it; reset
  carry-over re-applies the physical level only (no variant-derived level is created).

### Correctness — one must-fix (MF-1); otherwise sound
I constructed adversarial cases per area: write-surface type/empty/non-canonical inputs;
effort+model with/without variant on authoritative catalogs; unset-effort exemption on an
effort-unsupported runtime; missing/incomplete/revisionless catalogs; deterministic
runner selection (sorted by RunnerId); frozen-tuple preservation in failure evidence;
first-writer workflow store race (re-render + `DispatchCapabilityFence.Matches` re-check
before/after `StoreActiveWorkDispatchAsync` with re-pend); stale dispatch rejection before
execution with requeue that preserves the pending projection; heartbeat-catalog-replacement
refusal between resolution and claim. All hold. The `unsupported`-before-`incompatible`
precedence in the resolver is a documented, sensible choice.

### Consistency — checked, no issue
Append-only Orleans IDs, nullable TS fields for mixed-version catalogs, error-message style
mirrors `ValidateRuntime`, Web reader/writer extended symmetrically, CLI mirrors the server
set (duplicated locally, documented), docs (`pi.md`, `opencode.md`, `agent.md`,
`agent-sessions.md`, `cli-reference.md`) updated to match behavior rather than the plan
(including the honest non-authoritative OpenCode note).

### Tests — checked, but the OpenCode admission composition is untested (see MF-1)
The new coverage is extensive and the suites pass (I re-ran Server UnitTests: 2729/2729).
The gap is the missing production-shape test: a model-bearing OpenCode job (and a
`mohist/opencode` workflow task with a model option) against the runner's actual
registration shape. Every existing admission test either omits catalogs entirely or injects
authoritative ones, so the suite cannot see the hang.

## Observations (do not affect the verdict)

1. The progress notes' claim that the non-authoritative OpenCode entry "preserves current
   behavior" is inaccurate for model-bearing OpenCode agent jobs: prior behavior was
   execution, not pending. (This is context for MF-1, not a separate finding.)
2. Web editor: when the selected model's catalog no longer lists a stored canonical effort,
   the select displays "No reasoning effort" and saving silently stores `null`, dropping a
   still-valid canonical value. Signalling the drop (or keeping the value) would be less
   surprising.
3. `normalizeReasoningEffort` (pi/runtime.ts) treats a whitespace-only string as non-empty
   and fails the turn with `invalid-input` instead of treating it as unset. All write
   surfaces reject whitespace-only efforts earlier, so this is unreachable today.
4. Runner-side `staleCapabilityResult` purposely ignores nested `with.options.reasoningEffort`
   (non-Agent actions freeze effort unset; server-side fence covers template-preserving
   `${{ vars.agent }}` dispatches via the `Variables` payload). A one-line code comment
   documenting why nested effort is deliberately unchecked would help future readers.
5. `mo agent list` still renders no runtime/model/variant/effort columns; out of scope, fine.
6. Plan-level observations carried from `self-review.md` (pinned-runner admission
   explicitness, AC6 wording tension, EventCatalog terminology, T-001 spec-anchor
   spelling) were not addressed; none is required by the acceptance criteria.

<promise>FAIL</promise>