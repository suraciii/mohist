# Self-Review — Issue 452 (second pass)

Reviewer: plan-stage self-review of `openspec/changes/issue-452/` (proposal,
specs, design, tasks) against issue #452, after the first-pass findings were
fixed. Review only; no files changed except this one.

## Prior findings — all resolved

The first pass FAILed on five points. Each is now fixed and re-verified against
the code:

- **P1 (override write path / OQ1 unresolved) — fixed.** OQ1 is resolved (design
  D2/OQ1): the override is an optional `runtime` field on the manual launch
  request body, resolved `launchOverride ?? agentConfig.runtime ?? "opencode"`.
  T-001 owns the write/read path with an end-to-end AC. The `vars.agent.runtime`
  alternative is documented as rejected (it is the Workflow Inline Agent's options
  surface, consumed via `uses`, and nothing writes it). This also removed the
  launch→issue-variable coupling (resolves P5).
- **P2 (`AgentConfigSchema.Filter` second hardcoded list) — fixed.** Design D1
  and T-001 now name both surfaces (`AllowedKeys`/`Validate` and `Filter`'s
  `new[] { "model", "variant" }` at `AgentConfigSchema.cs:88`), with a
  round-trip-through-`Filter` AC.
- **P3 (AgentJob output `kind: "opencode"` hardcode) — fixed.** Design D4 and
  T-002 parameterize `buildAgentJobOutput` (`agent-job-executor.ts:250`) from the
  selected runtime, with an AC. Verified the server reads `failureCategory`, not
  `kind`, so this is labeling-correctness, not routing.
- **P4 (BREAKING vs additive contradiction) — fixed.** Proposal and design D6
  now consistently describe the `/opencode/models` change as additive (route +
  `{models, modelVariants}` shape preserved, optional `?runtime=` defaulting to
  opencode). No stale "BREAKING" remains outside this review file.
- **P5 (T-001 entanglement) — addressed** via the OQ1 resolution.

## Verification (this pass)

- **Coverage.** All five issue ACs map to tasks: AC#1 (config+launch T-001,
  execute+project T-002); AC#2 (launch-time override T-001); AC#3 (snapshot
  T-001); AC#4 (catalog T-003 + selectors T-004); AC#5 (executor surfaces
  failure T-002).
- **Consistency.** Override wording is aligned across proposal, spec
  (`Launch-time override precedence`), design D2, and T-001. OQ1 is marked
  RESOLVED everywhere; OQ2/OQ3 remain as bounded open questions with clear task
  directives. No stale "issue override / issue-scoped / BREAKING" references
  remain (the only "Issue-level … model selection" hits refer to the Web
  issue/stage model-picker UI, a distinct, legitimate concept).
- **Mechanics.** `tasks.json` is valid JSON; the 4-task graph is a DAG with
  dependencies pointing only to strictly-lower-priority tasks. Every spec
  requirement has ≥1 `#### Scenario` (4 hashtags, WHEN/THEN, SHALL/MUST); no
  3-hashtag scenarios. Each task has explicit, test-bearing acceptance criteria.
- **Load-bearing claims** (re-verified in the first pass, unchanged): `Id(4)`
  free on `AgentJobInput`, `Id(19)` free on `RoutedAgentLaunchPlan`;
  `AgentSessionInfo.Runtime` already returned to the runner so D5 holds.

## Observations (non-blocking)

These do not prevent building; listing for transparency.

1. **The launch-time override is API-only.** No Web task adds a per-launch
   backend affordance; the primary backend selection surface is the Agent editor
   (`agentConfig.runtime`, T-004). The literal AC#2 ("can change the backend for
   a launch; absent → config") is satisfied by the request field, but if product
   intends a UI-settable per-issue override, a Web affordance would be needed.
   Worth confirming intent; not a plan defect.
2. **T-002 has no explicit AC for a dispatch that omits `runtime`.** In practice
   every post-T-001 dispatch carries it (and the migration ships server+runner
   together), so an absent field is only a partial-rollout edge. A defensive
   "absent runtime → opencode" AC would harden it; not required for correctness
   in the coordinated release.
3. **Minor cross-reference.** T-001 also implements two `agent-job-runtime-execution`
   requirements (dispatch carries runtime; runtime-aware generic open/attach) but
   its `spec` field points to `agent-execution-backend`. Ownership is unambiguous
   in T-001's description/notes/ACs, so this is cosmetic.

## Verdict

The plan is internally consistent, buildable, fully covers the issue ACs, and
every prior finding is resolved with verified code references. Remaining items
are non-blocking observations. The plan is ready to build.

<promise>PASS</promise>
