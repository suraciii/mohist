# Self-Review: issue-560 (round 3 — disposition verification)

Re-review. Round 1 (full sweep) failed the plan on MF-1 (AC3 — model
recommendations — uncovered) and MF-2 (replay fingerprint could not cover
model/variant hints; spec/tasks contradicted the design). Round 2 verified
both fixed and PASSed (commit `b3a94f964`). Nothing has changed since: the
working tree is clean and HEAD *is* the round-2 review commit, so the
artifacts are byte-identical to what round 2 passed. This round therefore
(1) independently re-verifies round 2's load-bearing evidence rather than
trusting its prose, (2) re-runs the mechanical checks, and (3) probes for
pre-existing problems missed in rounds 1–2. Judged against the issue body
re-read first (User Voice, Product Shape, Domain Model, six acceptance
criteria, Non-Goals).

## Verdict

PASS. No must-fix finding is open or newly discovered; every claim round 2
rested on verifies against the code and artifacts. The plan is ready to
build.

## Disposition verification

### Round-1 must-fixes (fixed in round 2) — fix evidence re-verified independently

**MF-1 (AC3 coverage).** The artifacts contain what round 2 reported, and
the surfaces it cited are real:

- `proposal.md` carries the scope-honesty bullet (labeled Project default
  as the recommendation; catalog-backed selection and the full-options
  entry as the commitments; task-keyed recommendation engine out of scope
  because the catalog carries no per-purpose metadata).
- Web spec requirement
  `#inline-execution-configuration-when-no-project-default-exists` (anchor
  intact) specifies catalog-backed selection ("not a free-form model
  field") plus the labeled-recommendation and adjust-via-hints scenarios
  (3 scenarios). Code verified: `packages/web/src/shared/ui/ModelSelect.tsx`
  and `useAvailableModelIds`/`useModelVariants`
  (`packages/web/src/entities/settings/api/queries.ts:150,161`) exist and
  feed the definition editor today — reuse is real.
- CLI spec exit-behavior requirement names `mo agent model list`. Code
  verified: `MohistCliCommands.AgentModel.cs:17-18` registers `model list`
  ("List available coder model IDs for the runtime … use with --model").
- T-004 criterion 4 pins the catalog-backed inline path and the
  adjust-the-recommendation path with composer tests for both.

**MF-2 (replay fingerprint vs model/variant hints).** The mechanism's
load-bearing code facts all check out:

- `AgentLaunchCoordinatorRequest` occupies Orleans Ids 0–14 exactly
  (`AgentLaunchCoordinatorTypes.cs:147-191`, Prompt … TargetId), so the
  design's `Model` (Id 15) / `Variant` (Id 16) are genuinely next-free
  append-only ids.
- `AgentLaunchCoordinatorCodec.Fingerprint`'s canonical string folds
  exactly the fields D2 enumerates (prompt, AgentRef, Runtime,
  WorkspaceName/Path, Issue/Epic/Repo/Title, Origin, TargetId,
  attachments, connection-origin) joined with `\u001f` — the length-
  prefixed hint block is the right disambiguation, and invariant (b)
  (no-hint requests hash byte-identically to today's form) is
  implementable by contributing nothing when both hints are null.
- The grain stores `RequestFingerprint` at plan creation and recomputes
  and ordinal-compares on both the create-conflict path and resume
  (`AgentLaunchCoordinatorGrain.cs:100-102,192-194`) — confirming round
  2's point that the cross-deploy byte-identity invariant is mandatory,
  not optional, since definition-first/connection/mention/routed/spawn
  launches set no hints.
- `IAgentLauncher.ResumeIdempotentAsync` (`AgentLauncher.cs:474`) and
  `LaunchIdempotentAsync` (`:153`) exist as the composition points.
- Cross-artifact consistency holds: spec conflicting-replay scenario
  enumerates added/changed/removed `runtime`/`model`/`variant` → 409;
  T-002 criterion 6 pins the fingerprint inputs, the hint-conflict
  matrix, and the byte-identity invariant; D11 adds codec unit tests for
  both invariants.

### Round-2 observations — no action required

O-1..O-8 were below the must-fix bar by construction; the disposition
(no action) holds. O-7 re-confirmed: `design.md:434` glosses the pre-minted
id space as `agent_{16-hex}` while `StableToken`
(`Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()`) emits 32
hex characters (16 bytes); the stated *conclusion* — externally
indistinguishable from `agent_{Guid:N}` — is correct, only the gloss
miscounts. O-8 (D11 sentence splice around the codec-test insertion)
re-confirmed as cosmetic.

## Regression checks

No regressions are possible from changes: `git status` is clean and the
last commit touching the artifacts is the round-2 review itself
(`b3a94f964`), so the plan is unchanged since round 2's PASS. Mechanical
checks re-run and pass:

- Task graph: 5 tasks, unique ids, `dependsOn` acyclic and resolvable;
  ordering unchanged (T-001 → T-002 → T-003; T-004 after T-001/T-002;
  T-005 last and owns `npm run verify`).
- Specs: 21 requirements, 49 scenarios, every requirement has scenarios;
  every `specs/...#anchor` reference in tasks.json/design.md/proposal.md —
  fully-qualified and anchor-only — resolves.
- No stale scenario-slug references in any artifact (the old slugs appear
  only in this review's own history, describing the renames).

## Pre-existing-miss scan

Probed the areas rounds 1–2 relied on without deep code checks:

- **AC1/AC5's non-derived parts rest on real surfaces:** `AgentInfo`
  carries `Skills`, `AllowedSubagentAgentIds`, and `MaxConcurrentRuns`
  (concurrency intent); the web entity client and agent detail surfaces
  read them; the definition editor already carries the save-effect note
  ("Changes … apply only to Jobs created after saving. Executions already
  in progress … keep the configuration from launch." —
  `AgentProfileEditor.tsx:168`), which is AC5's commitment, preserved
  untouched by the plan.
- **AC2's state vocabulary is the codebase's:** `AgentReadinessConclusions`
  defines Ready / Unknown / Needs setup (`AgentReadinessService.cs:8-12`);
  the plan's Readiness work (default resolution, gap rules) operates on
  this real set.
- **D3/D7's reused machinery exists:** `EnsureNameAvailableAsync` /
  `AgentNameConflictException` (AgentGrain), `IAgentGrain.ArchiveAsync`
  (`IAgentGrain.cs:11`), and the definition-first route's closed field
  set (`prompt`/`context`/`attachments`) with required `Idempotency-Key`
  (`AgentSessionLaunchRoutes.cs:50-99`) — the design's "reuse verbatim"
  claims are grounded.
- Design Context's problem statement verified: the launch route accepts
  only `prompt`/`context`/`attachments` today, so `model`/`variant` hints
  are genuinely new caller-visible fields needing the fingerprint
  extension MF-2 prescribed.

No problem meeting the must-fix bar was found. Nothing new to justify a
round-1/2 miss.

## Observations (do not affect the verdict)

- **O-9 (AC1 "permissions" vocabulary):** the issue's AC1 lists 权限
  (permissions) among configurable/viewable Agent aspects; the domain has
  no dedicated per-Agent permission field — the launch-context/workspace
  binding plays the permission-scope role per the issue's own Domain
  Model, and the plan binds that scope per-launch (context validation +
  materialized snapshot). Rounds 1–2 read AC1 this way; recorded here so
  the disposition is explicit.
- O-1..O-8 from earlier rounds remain valid and unchanged.

## Summary

Round 3 verified rather than re-swept: round 1's two must-fixes are fixed
with evidence that survives independent re-checking against the code
(request Orleans Ids 0–14, ordinal fingerprint compare on resume,
`ModelSelect`/model-catalog hooks, `mo agent model list`), the artifacts
are unchanged since round 2's PASS, all mechanical checks pass, and the
pre-existing-miss probes found nothing at the must-fix bar.

<promise>PASS</promise>
