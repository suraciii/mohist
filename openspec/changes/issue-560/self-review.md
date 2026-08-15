# Self-Review: issue-560

First review (full sweep). Reviewed against the issue body (User Voice, Product
Shape, Domain Model, six acceptance criteria, Non-Goals) read before the
artifacts, and against the current codebase (`packages/server`,
`packages/web`, `packages/cli`, `docs/`).

Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, and all four
capability specs under `specs/` (21 requirements, each with scenarios; spec
format, task-graph validity, and every task/notes spec anchor verified
programmatically — all resolve).

## Verdict

FAIL. Two must-fix problems: one acceptance-criterion coverage gap and one
internal contradiction between the spec/tasks and the design's replay
mechanism. Everything else about the plan is strong and build-ready.

## Must-Fix Findings

### MF-1 — AC3 (model recommendations) is not addressed, covered, or scoped out

Issue acceptance criterion 3: 「模型选择按任务用途给出可理解的推荐，并保留查看完整选项的入口」
(model selection gives understandable recommendations by task purpose, with an
entry to view the full options).

- No artifact addresses it. `proposal.md` (Why / What Changes / Capabilities),
  all four specs, and all five tasks contain no model-recommendation behavior,
  no task-purpose model guidance, and no full-options entry on the task-first
  path. The inline composer controls are specified as free-form
  `provider/model` input (web spec `#inline-execution-configuration-when-no-project-default-exists`;
  T-004 note: "free-form is acceptable here"; design Open Question defers
  "model suggestions" composition to #556's rollout).
- The deferral has no committed owner for this criterion: #556 is a sibling
  backlog issue about CLI launch-time execution-config *preview*, not
  task-purpose model *recommendations* in creation, and no child issue of 560
  exists (`children: []`).
- The capability is cheap to cover or to scope: the Web definition editor
  already has a catalog-backed `ModelSelect` (`packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:235`)
  and `mo agent model list` exists (`packages/cli/Mohist.Cli/MohistCliCommands.AgentModel.cs`),
  so the task-first inline input could reuse the catalog (full-options entry)
  and surface the Project default as the labeled recommendation — or the
  proposal must explicitly de-scope AC3 with a named owner. As written, the
  plan silently narrows an explicit acceptance criterion of this issue:
  building the plan perfectly still leaves AC3 unchecked. That is incomplete
  relative to the issue's acceptance criteria.

### MF-2 — Replay conflict detection cannot cover `model`/`variant` hints; spec, tasks, and design contradict each other

- Spec `specs/agent-task-launch/spec.md#idempotent-replay-follows-the-launch-convergence-rules`
  requires: a replay under the same key "with a different request fingerprint —
  a changed prompt, context, attachments, `name`, or execution hints — SHALL be
  rejected 409 `launch_idempotency_conflict`", and its scenario "A conflicting
  replay is rejected" pins "a different prompt **or different execution hints**"
  → 409. Task T-002's acceptance criterion repeats it: "a changed
  prompt/context/attachments/name/**hints** under the same key → 409".
- Design D2 cannot deliver this. It folds only `AgentRef` = name hint and
  `Runtime` = runtime hint into the coordinator request ("never the derived
  Agent id or name"). `AgentLaunchCoordinatorRequest`
  (`Agent/Grains/AgentLaunchCoordinatorTypes.cs:147`) has no model/variant
  fields, and `AgentLaunchCoordinatorCodec.Fingerprint`
  (`AgentLaunchCoordinatorTypes.cs:290–322`) hashes only Prompt, AgentRef,
  Runtime, workspace/issue/epic/repo, Title, Origin, TargetId, attachments, and
  connection-origin fields. A replay with a changed `model` or `variant` hint
  produces an identical fingerprint, so `ResumeIdempotentAsync` returns the
  *original* outcome (silently ignoring the corrected hint) instead of 409.
- Consequence: T-002's acceptance criterion is unsatisfiable as designed, and
  the plan's own replay contract ("Replaying the same caller idempotency key
  returns the original outcome, following the existing launch convergence
  rules", proposal) diverges from the definition-first convergence rule, where
  every caller-visible request field that changes the outcome participates in
  the fingerprint. A user retrying after fixing a mistyped `--model` would get
  the old model with no error.
- Fix direction (pick one, in the artifacts): extend the fingerprint inputs
  append-only with the full caller-visible hint set (name/runtime/model/
  variant — e.g., a caller-hints payload on the coordinator request, folded
  like `StartupContext` is deliberately *not*), or narrow the spec scenario and
  T-002 criterion to the fields the fingerprint actually folds. The design must
  state the mechanism and the replay spec tests must pin it.

## Dimension Verdicts (full sweep)

### Coverage — FAIL (MF-1; details below)

Adversarial mapping of every issue acceptance criterion to plan coverage:

- **AC1** (task-language configure/view purpose, description, instructions,
  permissions, collaborators, concurrency): **partially covered, no issue.**
  Purpose/description/instructions are covered by task-first derivation
  (`agent-creation-defaults` requirements 1–2) plus refinement-after-launch.
  Permissions (`AllowedSubagentAgentIds`… see AC-note below), collaborators
  (`AllowedSubagentAgentIds`), and concurrency intent (`MaxConcurrentRuns`)
  are configurable/viewable today only through the existing definition editor
  (`AgentDetailPage.tsx`, profile editor), which the plan deliberately keeps as
  the deliberate-configuration path. The 「用任务语言」 framing for those three
  facets is not covered, but the existing surfaces satisfy 配置和查看 and the
  plan's derive-then-refine philosophy is the issue's own core idea. Recorded
  as Observation O-1 (disposition should be stated explicitly), not must-fix.
- **AC2** (list/detail distinguish not-configured / unknown / executable /
  insufficiently-executable): **covered.** `AgentListPage` and
  `AgentDetailPage` already render Ready / Needs setup / Unknown distinctly
  (incl. the `execution-config-failure` insufficient-executability path in
  `AgentReadinessService`), and the plan's readiness changes
  (`agent-creation-defaults#readiness-rules-…`) preserve and sharpen exactly
  these distinctions. No regression.
- **AC3** (model recommendations + full-options entry): **not covered —
  MF-1.**
- **AC4** (pre-launch display of repository/workspace/Issue/Epic context and
  permission scope): **mostly covered, no must-fix.** The composer already
  displays repo/workspace/Issue/Epic context as chips pre-launch
  (`AgentSessionComposerPage.tsx:35–52, 457`), and the plan foregrounds them
  (task-first reorder). The 「权限范围／预计影响范围」 tail has no defined
  product meaning today (no per-launch permission surface exists in the
  codebase or docs); the closest concept — workspace/repo binding — *is*
  displayed. Recorded as Observation O-2.
- **AC5** (explain when saved config takes effect for new Jobs): **covered.**
  `AgentDetailPage.tsx:567` already states "Instructions, Runtime, Model,
  Variant, and Skills edits apply only to Jobs created after saving…", and the
  web spec's `#refinement-after-launch` requirement restates and tests the
  semantics. No regression.
- **AC6** (CLI/Web consistent identity and execution scope): **covered.** The
  plan reuses `AgentSessionLaunchResponse` / `TableShape.AgentSessionLaunch`
  verbatim and materializes the execution config so both surfaces read the
  same self-describing definition.
- **User Voice / Product Shape core** (task-first creation and launch; the
  task comes first; two questions at most; no definition-first detour for a
  one-off delegation): thoroughly covered by all four capabilities.
- **Domain Model** (Definition = long-term identity; launch = per-execution
  context; edits affect only later Jobs; launch context becomes fact of that
  execution): covered — snapshot rules explicitly unchanged, launch context
  bound through the canonical pipeline.
- **Non-Goals**: respected. No new providers/runtimes (only `opencode`/`pi`),
  no concurrency claim/release mechanism, Slack path untouched (design
  Non-Goals).

### Correctness — FAIL (MF-2; everything else checked, no issue)

- Approach vs. each spec requirement: verified the launch-route composition
  (D1) against `AgentSessionLaunchRoutes.cs` (closed field set via presence
  binder, required `Idempotency-Key`, pre-minted session/input ids, resume-first
  flow) — the orchestrator design composes correctly.
- D2 deterministic Agent id: `agent_{StableToken}` is 32 hex chars
  (`AgentLaunchCoordinatorTypes.cs:260–265`), the same shape as today's
  `agent_{Guid:N}` (`AgentDefinitionRoutes.cs:35`) — externally
  indistinguishable, and `AgentGrain.CreateAsync` is keyed by grain id, so
  pre-minting/adoption is feasible. Checked, no issue.
- D3 validation cascade and error codes reuse existing helpers verbatim;
  determinable-before-create ordering is sound.
- D4 naming: `EnsureNameAvailableAsync` checks reserved built-ins and
  `GetByNameAsync` matches regardless of status (active+archived),
  case-insensitive — the derivation probe design is consistent. Checked, no
  issue.
- D5 storage: `ProjectRow` already uses nullable JSON columns
  (`LastRepositoryCommandJson`), so the additive migration matches
  conventions. The Project read route (`ProjectRoutes.cs:100`) is the stated
  read surface. Checked, no issue.
- D6 single resolver at three sites prevents Readiness/launch divergence;
  `runtime-invalid`/`model-reference-malformed` unmasked — matches
  `AgentReadinessService.StructuralGaps` codes. Checked, no issue.
- D7 crash-safe rollback: `BeginAbortAfterRejectionAsync`
  (`AgentLaunchCoordinatorGrain.cs:374`) already has abort acks plus a
  recovery reminder, so coordinator-owned archival with crash repair fits the
  existing structure; 503-pending never archives is correctly specified.
  Checked, no issue.
- Adversarial failure cases I could not construct: orphaned Agent on any
  determinable rejection (cascade runs pre-create), duplicate Agent on replay
  (pre-minted id + adoption), name race (bounded re-disambiguation), default
  flip mid-list (`MatchesCurrentDefinition` on resolved tuples). The one
  failure case that *does* construct is MF-2.

### Consistency with the current codebase — checked, no issue

Every named target exists and matches conventions: `Api/AgentSessionLaunchRoutes.cs`,
`Agent/Services/AgentLauncher.cs` (`LaunchIdempotentAsync(AgentInfo, …)`,
`EnsureLaunchableAsync`), `Agent/Grains/AgentLaunchCoordinatorGrain.cs`,
`AgentReadinessService`, `IAgentGrain.CreateAsync/ArchiveAsync`,
`AgentConfigSchema`, `InteractionWorkspaceProvisioner`, `ProjectRow`/`IProjectGrain`,
Web `AgentSessionComposerPage` (route `agent-sessions/new` in
`AppContent.tsx:76`), `useLaunchAgentSession` in `entities/agent`,
`AgentListPage` empty state, CLI `MohistCliCommands.Agent.cs` (no `start`
subcommand conflict; `BodyInputResolver`, `ResolveTypedAgentConfig`,
`TableShape.AgentSessionLaunch`, `X-Mohist-Launch-Origin` all present), and
all four docs files including `docs/agent-sessions.md` sections "Configure an
Agent" and "Launch Entry Points". Tasks format matches `issue-505`/`issue-589`
precedent; specs use correct `### Requirement` / `#### Scenario` structure
with zero missing scenarios.

### Task breakdown — checked, no issue

T-001→T-005: ids unique, dependencies acyclic and sensible (resolver before
route before rollback; web after server; CLI last because it owns the full
`npm run verify` gate). Each task has behavior-specific, verifiable acceptance
criteria, per-slice test gates (`test:fast`, web typecheck+tests,
`test:fast:cli`, `docs:check`), outputs, and doc ownership
(`agent-sessions.md` T-001/T-002, `agent-api.md` T-002, `web-ui.md` T-004,
`cli-reference.md`/`getting-started.md` T-005). All 21 spec anchors referenced
by tasks/notes resolve. Migration/rollback ordering (server-additive first,
web/CLI after) is correct. The only breakdown-level problem is that T-002's
criterion is unsatisfiable per MF-2.

## Observations (do not affect the verdict)

- **O-1 (AC1 tail):** 权限／协作者／并发意图 have no task-language surface in
  this plan; they remain definition-editor concepts. Defensible under
  derive-then-refine, but the plan should state this disposition explicitly so
  AC1's completion is not silently narrowed (same discipline MF-1 demands for
  AC3).
- **O-2 (AC4 tail):** 「权限范围和预计影响范围」 pre-launch confirmation has no
  defined product meaning today; the plan shows context and workspace/repo
  binding only. Worth an explicit interpretation in the proposal.
- **O-3 (spawn-hint coupling):** design D2 reuses `AgentRef` as the name-hint
  carrier; `BuildStartup` embeds `request.AgentRef` into the
  `mo agent spawn <agent-ref> …` hint string (`AgentLauncher.cs:743`), so a
  task-first launch (AgentRef = name hint or empty) produces a startup payload
  that differs from a definition-first launch of the same Agent at that one
  string — in tension with the "indistinguishable session metadata and source
  labels" requirement and T-002's equivalence spec. The equivalence tests will
  surface it; the design should pin the expected behavior (e.g., rebuild the
  startup with the created Agent's id, or document the exclusion).
- **O-4 (no first-class default-config surface):** the Project default is
  writable only via `PUT/PATCH /api/projects/{ref}/default-execution-config`;
  no Web settings UI or CLI command is planned to set it, yet the
  `execution_config_unresolvable` repair guidance tells users to "configure
  the Project default". The inline-hint path avoids the dead end, but a
  first-class configuration surface (or an explicit follow-up owner) would
  make the repair actionable for non-API users.
- **O-5 (determinable-rejection replay boundary):** the spec's replay
  requirement says replay "SHALL return the original outcome … or the original
  recorded rejection", while determinable pre-plan rejections (name conflict,
  unresolvable config) are re-evaluated on replay and can flip if state
  changed. The design documents this correctly as the definition-first
  convergence boundary; the spec wording could mislead a builder into thinking
  determinable rejections must be durably recorded. A clarifying sentence in
  the spec would help.
- **O-6 (verification scope):** plan-only review — no implementation or test
  suite was run; static checks performed: tasks.json JSON validity, task-graph
  acyclicity, spec-anchor resolution, requirement/scenario structure, and
  source-level verification of every design claim cited above.

## Summary

The plan's core — one task-first request composing the unchanged canonical
launch, deterministic derivation, Project default with one precedence rule,
orphan-free rejection with coordinator-owned rollback, task-first composer and
CLI — is well designed and verified against the codebase. It fails on one
explicit issue acceptance criterion it neither covers nor scopes (AC3, MF-1),
and on one replay-contract contradiction between its own spec/tasks and design
(MF-2). Both are bounded fixes to the artifacts.

<promise>FAIL</promise>
