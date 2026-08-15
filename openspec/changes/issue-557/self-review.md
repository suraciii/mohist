# Self-Review: Issue 557 Plan Artifacts (Round 1 — full sweep)

Reviewer verdict: **FAIL** (one must-fix finding). Artifacts reviewed:
`proposal.md`, `design.md`, `tasks.json`, `specs/agent-reasoning-effort/spec.md`,
`specs/runtime-reasoning-capability/spec.md`, judged against issue 557's goals and
acceptance criteria, with every codebase claim verified against the working tree.

## Must-Fix Findings

### MF-1. AC3 display coverage gap: Web Agent list and detail surfaces are absent from the plan

**Violates:** Acceptance criterion 3 — "Agent 的列表、详情和启动结果稳定显示
runtime、model、reasoning effort；真实 variant 单独显示" — and the Product Shape
goal "Agent 配置、可执行性状态和启动结果都能显示最终的模型与 reasoning effort".

The plan covers launch results (frozen effort in the AgentJob terminal result and
session model facts, T-002/T-007), the CLI detail view (`mo agent view` renders the
effort, T-001), and the Web *edit* surface (`AgentProfileEditor` effort control,
T-008). It does not cover the two existing Web *display* surfaces that render the
execution configuration today:

- `packages/web/src/pages/agent-list/ui/AgentListPage.tsx` — each row renders
  `model` and `variant` (via `readAgentModelAndVariant`, lines 46, 87–94).
- `packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx` — the "Agent
  Config" card renders static Runtime / Model / Variant rows (lines 543–559,
  testids `agent-detail-runtime` etc.), plus the edit-timing note that enumerates
  "Instructions, Runtime, Model, Variant, and Skills".

Neither page, nor `readAgentModelAndVariant` (the shared reader both use), is
mentioned anywhere in the proposal, design, specs, or tasks (grep confirms zero
references). The `AgentProfileEditor` is an edit modal (it takes an `open` prop),
not the detail display. An implementation that satisfies every spec scenario and
every task acceptance criterion would still ship list and detail pages where a
configured `reasoningEffort` is invisible — AC3 unmet on the primary user
surfaces, and a Pi agent carrying a saved effort would look identical to one
without.

**Fix:** add a spec scenario (under the Web requirement or a small display
requirement) plus T-008 acceptance criteria requiring the Web agent list rows and
the detail config card to render reasoning effort beside model with the true
variant still shown separately (extend `readAgentModelAndVariant` or equivalent),
with tests.

## Dimension Verdicts (first review, full sweep)

### Coverage — FAIL (MF-1); otherwise checked, no issue

- AC1 (independent effort on create/edit, explicit final value): T-001 (API/CLI/
  issue surface), T-008 (Web editor). Covered.
- AC2 (empty/unknown/incompatible values get an explicit pre-launch result):
  write-time canonical-set rejection (T-001) + resolver dispositions
  `unsupported_execution_configuration` / `incompatible_execution_configuration`
  as deterministic preflight failures (T-005/T-006). Covered.
- AC3 (list/detail/launch result display): launch result ✓, CLI detail ✓, Web
  detail/list ✗ → MF-1.
- AC4 (executability distinguishes missing config / unsupported / incompatible):
  `needs-setup` vs `unsupported_execution_configuration` vs
  `incompatible_execution_configuration` plus readiness gaps (T-005/T-006/T-007).
  Covered.
- AC5 (AgentJob freezes final config; later edits don't change it): T-002
  freezing + immutability tests, T-007 evidence. Covered.
- AC6 (temporarily unavailable model/effort → wait, never swap): `needs-setup` /
  `unavailable` leave work pending; exact matching, no fallback. Covered (see
  Obs-2 for a wording tension).
- Non-goals respected: no probing (catalog evidence only), no provider fallback,
  no permission/visibility work. The deliberate Pi variant break is explicitly
  declared, justified (D7), and operationally handled (risks + migration plan).
- Every spec requirement maps to at least one task; every task cites a spec
  anchor. The joint T-003+T-004+T-006 satisfaction of "Pi thinking-level variants
  are removed with no compatibility layer" is correctly decomposed (T-004's note
  explains the interim inert state; the spec scenario is conditioned on a
  complete catalog, which T-006 supplies).

### Correctness — checked, no issue beyond observations

- The resolver decision table matches the already-accepted prerequisite contract
  (`design/agent-runtime-reasoning-capability.md`,
  `openspec/changes/issue-557-runtime-reasoning-capability/`), including
  fail-closed treatment of legacy/incomplete entries and the
  pending-vs-terminal split.
- The claim-time fence closes the resolution→claim staleness window; the spec's
  fence requirement is unconditional over all claims, which (per the current
  claim flow) also covers pinned-runner and workspace-home admitted work, since
  delivery always goes through `TryClaimAgentJobAsync` in the poll loop (see
  Obs-1 for the residual test-coverage gap).
- D7's explicit-failure path is correctly conditioned on a complete catalog; a
  saved Pi thinking-level variant under an incomplete catalog stays pending
  (`needs-setup`), not wrongly terminal.
- The canonical set `off…max` exactly equals the union of Pi's native levels
  (`pi/sdk.ts`: reasoning models `minimal…max`, others `off`), so the
  private identity map in D6 is honest.

### Consistency with the codebase — checked, no issue

Every factual claim I could check is accurate:

- `AgentConfigSchema.AllowedKeys = {model, variant, runtime}`,
  `IssueAllowedKeys = {model, variant}` — exact.
- Orleans record ids: `AgentExecutionDefinition` uses Ids 0–5 (Id 6 free),
  `AgentJobInput` ends at Id 24 (Id 25 free), `RoutedAgentLaunchPlan` ends at
  Id 21 (Id 22 free), `RuntimeCatalogEntry` has only Ids 0–1 (2–5 free) — all
  match the design's assignments.
- `pi/runtime.ts` applies `options.variant` via `setThinkingLevel` at lines 156
  and 292 (design cites "~156 and ~292" — exact); `host.ts` publishes thinking
  levels as the `variants` map (line ~820).
- Referenced members all exist: `AgentLauncher.ResolveModelAndVariant` and the
  snapshot composition at ~line 829, `AgentExecutionSnapshotResolver`,
  `AgentReadinessService.{MatchesCurrentDefinition, StructuralGaps,
  IsConfigurationFailure}` (including the `variant-without-model` gap pattern
  D8 mirrors and the `_`→`-` normalization), `WorkflowItemTranslator` (vars.agent
  → options binding), `session-target.ts` / `followup-handler.ts` /
  `agent-job-executor.ts` (`readOptionalString`), `RunnerGrain._lifecycleGate`,
  `DispatchService.AddPendingDispatchesAsync` with the live readiness witness,
  `TryClaimAgentJobAsync(jobId, projectId)` / `TryClaimWorkflowAsync` (no
  expectation today), CLI `ValidateClearSetPair`, `ModelSelect`,
  `model-option-list`, `model-variants.ts`, `AgentProfileEditor`, pi
  `projector.ts` `model_change` → `model.resolved` payload.
- Spec format (`### Requirement:` / `#### Scenario:`, no delta headers) and
  `tasks.json` (vs `tasks.md`) both match existing changes in this repo
  (issue-505, issue-589 also use tasks.json).

### Task breakdown — checked, no issue beyond observations

- Ordering and dependencies are sound: T-001→T-002; T-003 independent;
  T-004 after T-002+T-003; T-005 after T-003; T-006 after T-002+T-003+T-005;
  T-007 after T-002+T-004+T-006; T-008 final slice owning `npm run verify`.
- Every task has concrete, testable acceptance criteria; the joint
  responsibility split for the Pi break is documented in notes.
- Residual: T-006's test list does not name the pinned/home admission paths
  (Obs-1).

## Observations (do not affect the verdict)

1. **Pinned-runner / workspace-home admission explicitness.** The prior build
   audit (issue comment, 2026-08-14) documented that `AgentJobGrain` elects
   pinned and workspace-home runners directly via `TryAdmitOnRunnerAsync`,
   bypassing the registry snapshot, and demanded pinned/home selection from the
   same snapshot. D5 names `DispatchService.AddPendingDispatchesAsync` as the
   resolver site — that method claims both assigned (pinned/home-elected) and
   eligible candidates today, and the spec's fence requirement is unconditional,
   so the mechanism covers these paths. But nothing in D5/T-006 names them, and
   T-006's test criteria omit pinned/home scenarios (e.g. an incompatible tuple
   on a pinned runner must become an explicit preflight failure, and the
   election-phase sourcing of `capabilityRevision` when the pinned runner's
   catalog is incomplete at election time is unspecified). Recommend explicit
   design note + tests in T-006.
2. **AC6 wording tension.** A model/effort explicitly absent from a *complete*
   catalog is a terminal preflight failure, while AC6's "暂时不可用时等待恢复"
   read literally could ask for waiting. The plan's evidence-based split (absent
   evidence → wait; explicit negative evidence → fail) is coherent, is already
   frozen in the accepted prerequisite contract, and avoids converting transient
   registration gaps into terminal errors. Fine to keep; worth one sentence in
   the design acknowledging the AC6 reading.
3. **`EventCatalog` terminology.** `EventCatalog` inventories CloudEvents type
   names; failure categories are `FailureCategory` strings classified elsewhere
   (e.g. `AgentReadinessService.IsConfigurationFailure`). "Register as EventCatalog
   failure categories" (proposal, T-006) is loose — harmless, but the implementer
   should place the new categories where failure categories actually live.
4. **Spec anchor nit.** `#write-surfaces-accept-reasoneffort` in T-001 should be
   `#write-surfaces-accept-reasoningeffort`.
5. **CLI `mo agent list`** renders only id/name/status/updatedAt today (no
   runtime/model/variant), so extending it is optional scope; MF-1's minimal fix
   is the Web surfaces. The session-timeline activity card likewise shows only
   `resolvedModel` (no variant) today — effort display there is a judgment call;
   the evidence *data* is covered by T-004/T-007.
6. **Open questions are scoped with defaults** (uniform fence for non-Agent
   workflow tasks; issue-level override UI stays Agent-profile-only in this
   change) — acceptable, recorded deferrals, and per-launch UX is correctly
   deferred to issue 556.

## Summary

The plan is unusually well-grounded: every codebase reference I verified is
accurate (including Orleans ids and line numbers), the architecture faithfully
implements the accepted prerequisite capability contract, the deliberate
breaking change is responsibly handled, and the task graph is well-ordered and
verifiable. The single must-fix is a bounded coverage gap: AC3's list/detail
display requirement is unmet for the Web Agent list and detail surfaces, which
today render model/variant and are never mentioned in the plan.

<promise>FAIL</promise>
