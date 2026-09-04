# Self-Review — Issue #560 (Agent 创建和启动：任务导向的产品体验)

First review: full sweep. Issue body, proposal, design, four capability specs, and tasks.json were read and checked against the current codebase (`packages/server`, `packages/web`, `packages/cli`), not against the plan's own framing.

## Verdict

PASS. Every issue goal and acceptance criterion is covered by a spec requirement with an owning task; the design's claims about the current code were verified accurate at the file level; no must-fix problem was found. Reservations are recorded as observations below and do not affect the verdict.

## Must-Fix Findings

None.

## Issue Acceptance Coverage — checked, no issue

Each acceptance criterion mapped to spec + owning task:

1. **任务语言配置/查看目的、说明、指令、权限、协作者、并发意图** → `agent-task-profile` (definition fields, closed vocabulary, task-first structure) → T-001, T-005, T-008. Verified the gaps are real: `Domain.Agent` has no `Purpose`/`Permissions`; `AgentProfileEditor.tsx` sends only name/instructions/skills/agentConfig while the server binder already accepts description/`allowedSubagentAgentIds`/`maxConcurrentRuns`.
2. **列表/详情显示尚未配置完成、不可执行、未知、可执行** → `agent-executability-status` (exactly four states, matching the issue's four verbatim) → T-002 (derivation + gate), T-006 (web list/detail), T-009 (CLI list/view). The current three-conclusion projection (`AgentReadinessConclusions.Ready/NeedsSetup/Unknown`) and the `Readiness:` inline text in `AgentListPage` / `ReadinessCard` in `AgentDetailPage` confirm the change is needed and the surfaces named exist.
3. **按任务用途的模型推荐 + 完整选项入口** → `agent-model-guidance` (advisory, catalog reachable) → T-004 (server catalog + endpoint + CLI), T-005 (ModelSelect recommendations group). The existing catalog surface (`/opencode/models` with `runtime` param, `mo agent model list`, `ModelSelect`) is extended, not replaced — consistent with the non-goal of no new providers/runtimes.
4. **启动前展示 repository、workspace、Issue/Epic 上下文、权限范围** → `agent-launch-scope` (one resolver, preview projection, caller confirmation) → T-003, T-007 (composer confirm panel), T-010 (CLI print + confirm/`--yes`). Verified the current code has no repository validation (`ValidateContextAsync` checks issue/epic/workspace only) and no preview path — the resolver genuinely adds the missing fact.
5. **保存后说明生效时间，不改变已运行 Job 的事实** → `agent-task-profile` effective-time requirement + D6 snapshot discipline → T-005/T-008 (save statement; existing web dialog copy extended), T-003 (immutability + idempotent-replay tests on `AgentJobInput`). The launcher's existing copy-at-launch discipline (model/runtime/instructions/skills) makes the statement true rather than aspirational.
6. **CLI 与 Web 一致的身份与执行范围** → executability "one Server projection" requirement + D8 + shared launch-scope projection → T-006, T-009, T-011 cross-surface verification.

Non-goals respected: no new provider/runtime, no concurrency claim/release, no Slack install in the create form; the launch body stays `prompt`/`context`/`attachments` (binder `AllowedTopLevelFields` unchanged, override rejection retained).

Every requirement in all four specs is referenced by at least one task's `spec` anchor; all anchor slugs resolve to actual requirement headings; all 44 scenarios fall inside task acceptance criteria (T-011 additionally requires a scenario-to-test trace).

## Correctness — checked, no issue

Adversarial construction attempts and their outcomes:

- **Gate semantics regression?** Today `EnsureLaunchableAsync` rejects only `Needs setup`, which already folds execution-config failures (current `Evaluate` maps `IsConfigurationFailure` evidence to `Needs setup`). Splitting that conclusion into `not-configured` + `not-executable` and gating both preserves the accepted/rejected set exactly; `unknown`/`executable` accept, matching today's `Unknown`/`Ready` accept. `AgentConnectionDispatchDecision.For` (Slack paths) is included in the rename per D3, so no launch path keeps a stale verdict vocabulary.
- **Preview ≠ dispatch scope (TOCTOU)?** Both call one resolver; the Web composer reuses the dispatch idempotency key so the per-session workspace name (`ResolveWebWorkspaceNameAsync(projectId, preMintedSessionId)`, derived from `(projectId, idempotencyKey)`) is identical at preview and dispatch; dispatch persists the dispatch-resolved scope, so the recorded fact is always the true one. Acceptable residual window, honestly recorded as a risk.
- **Replay returns the recorded scope?** The coordinator's canonical-plan discipline is invoked; T-003's acceptance criteria pin the replay case explicitly, and the observation surface reads job-owned facts.
- **Unknown rendered as error?** The projection carries the pending-launch note; T-006/T-009 have explicit neutral-rendering criteria.
- **Availability merged into executability?** Both specs and D3/D8 keep the services, endpoints, and renderings separate; current code already renders them separately (`AgentDetailPage` `ReadinessCard` + `AvailabilityCard`), so the plan preserves an existing separation rather than inventing one.
- **Permissions as launch-time override?** Not possible: vocabulary is definition-write-boundary validated only, echoed from the definition at launch, and the launch binder rejects undeclared fields before any session/job exists.
- **Model guidance becoming validation?** Explicitly advisory (D4 + spec requirement 3); missing model surfaces only through `not-configured`, which matches the current deriver behavior (`model-missing` gap).

## Consistency with Current Codebase — checked, no issue

Every code-facing claim in proposal/design was spot-checked and is accurate:

- `Agent` domain model fields, `AgentRow` JSON `State` via `AgentGrain` + `IStateStore` (no DB migration needed), `AgentCreateData`/`AgentUpdateData`/`AgentInfo` as append-only `[property: Id(n)]` Orleans records with free next ids.
- `AgentDefinitionRoutes` presence-based `Fields` binder and the `ValidateMaxConcurrentRuns` pattern the permission validation mirrors.
- `AgentSessionLaunchRoutes` body binder (rejects undeclared top-level fields), workspace resolution order (explicit > CLI default > Web per-session), context validation gaps, and the unvalidated repository reference.
- `AgentJobInput` currently uses ids 0–24; a new `LaunchScope` field is append-only-safe, matching the repo's Orleans convention and the rollback claim.
- Web surfaces named in the plan exist as described (`AgentProfileEditor` parity gap, `AgentListPage` `Readiness: {readiness}` inline text, composer immediate dispatch, `launch-feedback.ts` `agent_needs_setup` mapping, local conclusion fallback in `entities/agent`).
- CLI surfaces exist as described (`MohistCliCommands.Agent.cs` option sets and clears, `--agent-config` retired, `mo agent view` server-authoritative rendering, `mo agent launch` direct POST with `agent_needs_setup` handling, `--epic` as string vs the API's integer).
- Referenced sibling issues #555/#556/#558 exist with the scopes the plan attributes to them.
- No-compat atomic rename, `npm run verify` gate, spec file format, and tasks.json schema all match repo conventions (compared with issue-589/issue-505 artifacts and `docs/actions/openspec.md`).

## Task Breakdown — checked, no issue

Ordering is dependency-correct: T-001 (definition fields) → T-002 (executability) → T-003 (launch scope); T-004 independent; surfaces (T-005–T-010) depend only on the server contracts they consume; T-011 integrates after all surfaces. Each task has testable acceptance criteria tied to spec scenarios and `npm run verify`; T-011 additionally requires the scenario-to-test trace and cross-surface parity checks (set-in-one-surface-visible-in-other, cleared-stays-cleared), which is where AC6 is actually proven.

## Observations

- **Composer readiness-gating migration is implicitly owned.** `AgentSessionComposerPage` gates pre-launch on `readinessConclusion === 'Needs setup'` and renders a "Readiness: Needs setup"/"Unknown" banner (lines ~250–253, 416–448, 514). No task names this block; T-005/T-006 update the `entities/agent` conclusion union, which turns those comparisons into compile errors, and T-011 is the seam catch-all — so the plan still lands consistent, but the composer's pre-launch executability banner would ideally be an explicit T-007 acceptance criterion.
- **`unknown` definition wording vs derivation.** The executability spec defines `unknown` as "no execution evidence that matches the current definition", while D3 (and current code) also maps *non-config-failure* matching evidence to `unknown`. The observable behavior satisfies the spec's normative clause ("surfaces MUST NOT infer success or failure"), but the definition bullet and T-002's tests would be cleaner if the non-config-failure case were stated in the spec and pinned by a test.
- **Non-interactive dispatch paths get no consolidated `LaunchScope`.** Routed/mention launches persist issue/epic/workspace as separate `AgentJobInput` facts today, but the new `LaunchScope` snapshot and permission echo apply to the manual (composer/CLI) path only, so the observation DTO will show an absent scope for those launches. Defensible — those paths have no caller-confirmation moment and no spec scenario covers them — but worth stating in the observation surface's rendering.
- **Epic reference typing seam is pre-existing.** The CLI serializes `--epic` as a JSON string while the launch binder requires an integer (so a string epic arguably 400s today); the design flags normalization as an open question and T-003's note defers the choice. T-003's epic-preview acceptance criterion forces the resolution during implementation; pick the fix location (CLI serialization vs binder coercion) then.
- **T-005 and T-006 both touch `entities/agent`** (API/model). Sequenced by priority, so no conflict, just a boundary overlap to respect during execution.
- **`avatar` remains CLI-only editable** — a pre-existing single-surface field outside the task-profile set the parity requirement covers; out of scope, noted so it is not mistaken for a new regression.
- **`MatchesCurrentDefinition` does not compare purpose/permissions**, so a permissions-only edit leaves prior execution evidence "matching" and a previously `executable` agent stays `executable`. Reasonable while permissions are declaration-only (not enforced, not executed); revisit if enforcement lands.

<promise>PASS</promise>
