# Self-Review: issue-560 (round 2 — disposition verification)

Re-review. Round 1 (full sweep, preserved in git history at `8c9397593`)
found two must-fix problems: MF-1 (acceptance criterion 3 — model
recommendations — neither covered nor scoped) and MF-2 (replay conflict
detection cannot cover `model`/`variant` hints; spec/tasks contradicted the
design). This round verifies the dispositions recorded in `progress.txt`
(both marked FIX, commits `2f4a677a8`, `a3b1e87cc`, `808c4ab8a8`), checks the
fixes for regressions, and scans for pre-existing problems missed earlier.
Judged against the issue body re-read first (User Voice, Product Shape,
Domain Model, six acceptance criteria, Non-Goals).

## Verdict

PASS. Both must-fix findings are fixed properly and verified against the
codebase; the fixes introduced no regressions; no new must-fix problem
exists. The plan is ready to build.

## Disposition verification

### MF-1 — AC3 (model recommendations + full-options entry): FIXED (coverage, not de-scope)

The fix is exactly the coverage direction MF-1 prescribed, and every surface
it names exists in the codebase:

- **Web** (`web-agent-task-composer` spec, requirement
  `#inline-execution-configuration-when-no-project-default-exists`, anchor
  preserved): inline Model selection is now catalog-backed — choices from
  the Project's available models for the selected Runtime, with variants,
  through the same catalog-backed selection the definition editor uses,
  explicitly "not a free-form model field". With a default configured, the
  composer presents it as the **labeled recommended execution configuration**
  ("for tasks in the Project" — the task-purpose framing) with an optional
  adjust affordance opening the same catalog; launching unadjusted submits no
  hints. Verified in code: `ModelSelect` (`packages/web/src/shared/ui/ModelSelect.tsx`),
  `useAvailableModelIds`/`useModelVariants`
  (`packages/web/src/entities/settings/api/queries.ts:150,161`) power
  `AgentProfileEditor.tsx:235` today, so reuse is real, not speculative.
- **CLI** (`cli-agent-task-launch` spec, exit-behavior requirement + its
  missing-configuration scenario): the guidance now names `mo agent model
  list` as the entry to view the available models. Verified:
  `packages/cli/Mohist.Cli/MohistCliCommands.AgentModel.cs` provides
  `model list` ("List available coder model IDs for the runtime … use with
  --model").
- **Scope honesty:** `proposal.md` now carries an explicit bullet: the
  labeled Project default is the recommendation; the catalog carries no
  per-purpose model metadata, so a task-keyed recommendation engine is out
  of scope, with the recommendation and the full-options entry as the
  commitments. The design's open question no longer defers the criterion to
  #556 — catalog-backed selection and the labeled recommendation are
  committed here; the #556 preview endpoint is an optional enhancement.
  T-004's old note ("free-form is acceptable here") is removed and replaced
  by a catalog-backed criterion with tests for both the no-default inline
  path and the adjust-the-recommendation path.

AC3 is now addressed: an understandable, purpose-labeled recommendation plus
a full-options entry on both surfaces. No residual gap.

### MF-2 — replay fingerprint vs model/variant hints: FIXED (extend, not narrow)

The chosen mechanism is verified against the code at every load-bearing
point:

- `AgentLaunchCoordinatorRequest` uses Orleans Ids 0–14
  (`AgentLaunchCoordinatorTypes.cs`; Prompt…TargetId), so the design's
  `Model` (Id 15) / `Variant` (Id 16) are genuinely next-free append-only
  ids.
- The grain stores `RequestFingerprint` at plan creation and **recomputes
  and ordinal-compares on resume**
  (`AgentLaunchCoordinatorGrain.cs:100,193` — `string.Equals(…,
  StringComparison.Ordinal)`). The design's invariant (b) — a request with
  no model/variant hint hashes byte-identically to today's canonical form,
  so plans in flight across the deploy resume without false conflicts — is
  therefore the *correct* compatibility requirement, not an optional nicety,
  and the fix identified it unprompted. Definition-first, connection,
  mention, routed, and spawn launches set no hints, so their fingerprints
  are unchanged.
- Invariant (a) — added, changed, or removed hint → different fingerprint →
  409 — closes exactly the silent-ignore trap MF-2 constructed (retry after
  fixing a mistyped `--model` now conflicts instead of replaying the old
  model). The length-prefixed hint block avoids delimiter ambiguity between
  model and variant.
- Consistency restored across artifacts: the spec's conflicting-replay
  scenario enumerates "a changed, added, or removed `runtime`, `model`, or
  `variant`" → 409; T-002 criterion 6 pins the fingerprint inputs, the
  hint-conflict matrix, and the no-hint byte-identical invariant; D11 adds
  codec unit tests pinning both invariants. Spec, design, and tasks no
  longer contradict each other, and T-002's acceptance criteria are now
  satisfiable as designed.
- The route-flow composition is real: `IAgentLauncher.ResumeIdempotentAsync`
  exists (`AgentLauncher.cs:474`) alongside
  `LaunchIdempotentAsync(AgentInfo, …)` (`:153`).

### Observations O-1..O-6 — no action required, consistent with round 1

`progress.txt` records them as requiring no action. They were observations
by definition in round 1 (below the must-fix bar) and remain so; the
disposition holds. O-4 (no first-class UI/CLI surface to *set* the Project
default) stays an observation: the default is optional for launching (the
inline-hint path works without it), the write surface exists via the API,
and the spec's repair guidance names both paths.

## Regression checks on the fixes

- Task graph: 5 tasks, ids unique, `dependsOn` acyclic and resolvable;
  ordering unchanged and still correct (T-001 resolver → T-002 route →
  T-003 rollback; T-004 web after T-001/T-002; T-005 last, owns
  `npm run verify`).
- Spec anchors: every `specs/...#anchor` referenced by task `spec`/`notes`
  fields and by `design.md`/`proposal.md` resolves (checked
  programmatically; 21 requirements, 49 scenarios, every requirement has
  scenarios).
- The web-spec scenario renames ("No default asks inline" → "…from the
  catalog"; "A default launches without questions" → "A default is
  presented as the labeled recommendation") plus the new adjust scenario:
  no artifact references the old scenario slugs (grep clean); the
  requirement anchor referenced by T-004 is unchanged.
- Cross-artifact consistency of the new behavior, verified line-by-line:
  spec ↔ design D9/D10 ↔ T-004/T-005 ↔ proposal all state the same
  model-selection contract (labeled recommendation, adjust-via-hints,
  catalog-backed, no hints when unadjusted; CLI names `mo agent model
  list`). The adjust-submits-hints behavior composes correctly with the
  extended fingerprint (adjusted hints participate in the replay conflict —
  intended per D2 invariant (a)).
- The fingerprint edits touch nothing else: D2's other mechanism
  (pre-minted `agent_{StableToken}` id, crash-window adoption), D3–D8, the
  migration plan, and rollback are byte-identical to the reviewed version
  except for the intended fix hunks (diff `8c9397593..HEAD` reviewed in
  full).

## Pre-existing-miss scan

Re-checked the dimensions from round 1 in the areas the fixes touched and
found no problem meeting the must-fix bar. Two cosmetic notes (below) were
present in round 1 and missed there; neither affects correctness or
completeness, so they are recorded as observations, not must-fix — the miss
is immaterial because a builder following D2 uses `agent_{StableToken(…)}`
verbatim regardless of the prose gloss.

## Observations (do not affect the verdict)

- **O-7 (`agent_{16-hex}` gloss):** design Risks says the pre-minted id
  space is `agent_{16-hex}`; `StableToken` emits
  `Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()` — 16 *bytes*
  = 32 hex characters, matching `agent_{Guid:N}` exactly. The stated
  conclusion (externally indistinguishable) is right; the gloss miscounts.
  Fix the wording opportunistically during implementation.
- **O-8 (D11 sentence splice):** the codec-test insertion left "…plus codec
  unit tests pinning the two fingerprint invariants (…). Unit tests cover
  `AgentTaskDefinitionFactory`…" reading as two adjacent sentence stems;
  harmless, worth a copy-edit pass.
- Round-1 observations O-1 through O-6 remain valid and unchanged.

## Summary

Round 1 failed the plan on one uncovered acceptance criterion and one
internal contradiction. Both were fixed in the direction the review
prescribed — AC3 covered with real, verified surfaces (`ModelSelect`,
`useAvailableModelIds`/`useModelVariants`, `mo agent model list`) and the
replay fingerprint extended append-only with the cross-deploy
byte-identity invariant that the coordinator's actual ordinal-compare-on-
resume behavior makes mandatory. Dispositions verified, no regressions, no
new must-fix problems.

<promise>PASS</promise>
