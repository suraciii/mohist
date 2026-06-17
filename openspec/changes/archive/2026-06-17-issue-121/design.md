## Context

Issue #121 sets out to make the Coder Agent Tab's selected model actually reach the opencode runner, by unifying how agent config is stored and resolved across the three variable layers (global `config.jsonc` → project profile → issue profile).

A read of the current code revises the issue body's premise. The issue body describes `IssueWorkflowProfile` as carrying special `AgentConfig` / `StageAgentConfigs` fields and a `HydrateAsync` agent merge. The actual codebase is already further along:

- **Persistence is already unified.** Both `IssueWorkflowProfileRow` and `ProjectWorkflowProfileRow` store a single `Variables` column of `VariableBundle` JSON (`{ vars, stages }`). There are no `AgentConfig` / `StageAgentConfigs` columns to remove (`IssueWorkflowProfileRow.cs`, `ProjectWorkflowProfileRow.cs`).
- **The variable API is already generic.** `PATCH /issues/{n}/workflow-profile/variables` takes a `VariableBundle` and calls `IssueWorkflowProfileManager.PatchVariablesAsync` (`IssueRoutes.WorkflowProfile.cs:92`). The web client already writes `vars.agent` and `stages.<stage>.vars.agent` to it (`entities/issue/api/client.ts:186,193`).
- **`VariableBundle` already implements the generic merge.** `VariableBundle.Patch` / `MergeAll` deep-merge `vars` and each `stages.<stage>.vars` symmetrically (`Workflow/Domain/VariableBundle.cs`). The merge machinery the spec requires already exists.
- **Runtime already merges project + issue generically** via `WorkflowProfileManager.LoadVariablesAsync` → `MergeAll(projectBundle, issueBundle)` (`WorkflowProfileManager.cs:79`). The `agent` key is not special there.
- **The agent-specific interface methods are dead code.** `IIssueWorkflowProfile.BuildVariables(globalAgentConfig)` and `BuildStageVariables(globalStageAgentConfigs)` and their helpers in `MohistDefaultIssueWorkflowProfile` have no callers (verified by grep). They are the only agent-specialised code path.
- **Read-model `AgentConfig` / `Model` / `StageModels` are projections**, derived from the bundle in `IssueQuerier.ApplyIssueWorkflowVariables` (`IssueQuerier.cs:396`), not independent storage. The `Issue` domain entity has no such persisted fields.

The real gap that produces the bug: **the global layer (`config.jsonc`) is disconnected from the `VariableBundle` path.** `LoadVariablesAsync` merges project + issue but never includes global config, and `IssueGrain.StartWorkflowAsync` (the T1 point) patches only context vars (`mohist`, `issue`, `project`, …) into the issue profile — it never merges project or global agent config in. So when a project has not set `vars.agent`, nothing carries the global `config.jsonc` agent into the effective variables, and the runner falls back to opencode defaults. The fix is to make T1 the single, generic merge point that folds global + project into the issue's `Variables`.

Stakeholders: Coder Agent Tab (web UI), opencode runner (model dispatch), and the issue/profile persistence layer. Constraints: do not regress `ConfigServiceSpecs`, `VariableScopeSpecs`, `VariableBundleSpecs`, or `MohistDefaultWorkflowProfileSpecs`.

## Goals / Non-Goals

**Goals:**
- Make T1 (issue start) the single point that produces the issue's effective `Variables` by generically merging the global `VariableBundle` and the project `VariableBundle` (project wins, global fills gaps, symmetric for `vars` and each `stages.<stage>.vars`).
- Expose global `config.jsonc` as a `VariableBundle` (`vars.agent`, empty `stages`) so it can participate in that merge as an ordinary layer.
- Have runtime read the pre-merged issue `Variables` directly; remove the agent-specialised resolution path.
- Delete the dead agent-specific interface methods and helpers so no `agent`-keyed code remains.
- Keep the UI-facing `Model` / `StageModels` / `AgentConfig` projections working as derived views of `Variables`.

**Non-Goals:**
- No database schema change and no data migration script — persistence is already unified (see Migration Plan).
- No change to the `POST/PATCH /api/issues` issue-entity `model` / `stageModels` fields or their http-api requirement; those remain a separate, coexisting path.
- No change to prompt resolution, template resolution, or `ResolvedTemplate.EmbeddedVariables`.
- No new public schema (YAML/JSON Schema) for `Variables` in this issue (flagged as tech debt in the issue, deferred).
- No refactor of `ConfigRoutes` / `OpencodeRoutes` beyond what is needed to feed the global bundle into T1.

## Decisions

### Decision 1 — T1 is the single merge point; runtime reads the issue snapshot
At `IssueGrain.StartWorkflowAsync`, compute the issue's `Variables` as `VariableBundle.MergeAll(globalBundle, projectBundle)` then patch the context vars (`mohist`, `issue`, `project`, `repository`, `openspecChangeName`, `openspecChangeDir`) on top, and persist that as the issue profile's `Variables`. Runtime variable resolution reads the issue layer directly.

- **Rationale:** Matches the spec ("effective agent configuration SHALL be fixed once, at issue creation"). Eliminates the runtime project-merge entirely, so there is exactly one resolution path and one place that knows about layering. `agent` falls out as an ordinary merged key.
- **Alternative considered — keep runtime live-merge of project + issue and only inject global.** Rejected: it leaves two merge sites (T1 context patch + runtime project merge), which is exactly the asymmetry the issue blames for the original bug, and it conflicts with the spec's snapshot semantics.
- **Consequence (snapshot):** changes to project or global `Variables` after an issue is created do **not** retroactively change that issue's effective variables. This is intended (spec scenario "already-created issues SHALL retain their previously merged Variables"). New issues pick up the new values.

### Decision 2 — Reuse `VariableBundle.MergeAll` as the generic `MergeVariables`
No new merge code. `MergeAll(global, project)` already deep-merges `vars` and each `stages.<stage>.vars` symmetrically with the right precedence (later layer wins; `project` is passed last). The spec requirement that `vars` and `stages.<stage>.vars` share one merge pattern is satisfied by construction.

- **Rationale:** avoids a bespoke `MergeVariables` that could re-introduce an `agent` special case. Symmetry is enforced by having a single recursive `DeepMerge` for both levels.
- **Alternative — write a dedicated `MergeVariables(projectVars, globalVars)`.** Rejected as a needless wrapper; `MergeAll` already is that function.

### Decision 3 — Global config exposed as a `VariableBundle` via a `ConfigService` adapter
Add `ConfigService.GetVariables()` returning a `VariableBundle` built from the existing `GetAgentConfigAsync()` placed at `vars.agent`, with `stages` always empty. This is the global layer fed into Decision 1.

- **Rationale:** `config.jsonc` already stores `agent` (and legacy `model`); the adapter just reshapes it into the bundle shape. Stages are deliberately empty globally because stage names are project-specific (spec requirement).
- **Alternative — migrate `ConfigRoutes` / `OpencodeRoutes` off `GetAgentConfigAsync` entirely now.** Rejected for scope; those routes serve the Settings UI and are left as-is in this issue (Open Question).

### Decision 4 — Runtime merge collapses to a direct issue read
`WorkflowProfileManager.LoadVariablesAsync` currently does `MergeAll(projectBundle, issueBundle)`. Once T1 folds project into the issue snapshot, this becomes a read of the issue layer only. Definition-embedded variables (`ResolvedTemplate.EmbeddedVariables`) continue to be merged at the runner boundary as today; they are orthogonal to profile Variables.

- **Rationale:** removes the second merge site and the last place that re-derives layering at runtime.
- **Risk:** `VariableScopeSpecs` and `IssueVariableBuilderSpecs` encode the old project+issue runtime merge and must be updated to assert the T1 snapshot instead.

### Decision 5 — Delete the dead agent-specific profile methods
Remove `IIssueWorkflowProfile.BuildVariables(..., globalAgentConfig)` and `BuildStageVariables(..., globalStageAgentConfigs)` plus the private `BuildAgentConfig` / `MergeAgentConfig` / `MergeStageAgent` / `MergeVarsJson` helpers in `MohistDefaultIssueWorkflowProfile`. These are the only agent-keyed code paths and have no callers.

- **Rationale:** satisfies the spec requirement "no agent-specific code path exists" and the method-signature scenario.
- **Guard:** re-run the caller grep before deletion; keep an eye on `WorkflowGrainTestHelpers` / `GrainTestConfig` which may stub the interface.

### Decision 6 — Keep `IssueReadModel` projections; clarify they are derived
`IssueReadModel.AgentConfig` / `Model` / `StageModels` stay, still produced by `IssueQuerier.ApplyIssueWorkflowVariables` from the (now T1-merged) bundle. They are a read-only convenience for the UI, not a source of truth.

- **Rationale:** `IssueDetailPage.tsx:1073` reads `issue.model` / `issue.stageModels` to render the selector; removing them would force the UI to reparse the bundle. Keeping them as derived projections preserves the UI with no behaviour change.

## Risks / Trade-offs

- **[Snapshot hides late project/global edits from existing issues]** → Mitigation: documented, spec-endorsed behaviour; a re-open or "re-sync variables" action can re-run the T1 merge if needed (out of scope here; flagged in Open Questions).
- **[Collapsing `LoadVariablesAsync` to issue-only changes runtime behaviour for all variables, not just `agent`]** → Mitigation: update `VariableScopeSpecs` / `IssueVariableBuilderSpecs` to assert the new T1-then-direct-read flow; keep `ConfigServiceSpecs`, `VariableBundleSpecs`, `MohistDefaultWorkflowProfileSpecs` green as gates.
- **[Definition-embedded variables (`EmbeddedVariables`) could be dropped if the runtime merge is naively replaced]** → Mitigation: `EmbeddedVariables` merges at the runner boundary, independent of profile Variables; do not touch that path. Add a spec asserting embedded vars still apply after the change.
- **[Dead-code deletion may break test stubs that implement `IIssueWorkflowProfile`]** → Mitigation: update `WorkflowGrainTestHelpers` / `GrainTestConfig` test doubles when the interface changes.
- **[Two config surfaces (`config.jsonc` agent vs. Settings UI routes) can drift]** → Mitigation: `GetVariables()` is the single read path used by T1; `ConfigRoutes` keeps writing the same `config.jsonc` keys, so both surfaces stay consistent.

## Migration Plan

**No database migration is required.** Both profile rows already store `Variables` as `VariableBundle` JSON; `AgentConfig` / `StageAgentConfigs` exist only as derived fields on read/transfer models. The spec's migration requirement is therefore satisfied by a one-time **verification** rather than a data-moving script:

1. Add a migration-verification query/test that scans `IssueWorkflowProfile` rows and asserts every row's effective agent config is reachable through `Variables` (i.e. no row relies on a field outside `Variables`). Because storage is already unified, this is expected to pass on day one.
2. Deploy the code change (T1 merge + `ConfigService.GetVariables` + dead-code removal) behind the normal server restart.
3. On restart, existing issues keep their already-stored `Variables`; only newly started issues get the T1 global+project snapshot. This is the intended snapshot behaviour.

**Rollback:** revert the code change. Existing issue rows are untouched (no schema change, no data rewrite), so rollback is clean. The only behavioural difference on rollback is that newly created issues during the window did not snapshot the global layer — they re-run against the old runtime path.

If, contrary to expectation, the verification query finds rows whose agent data is not in `Variables`, treat that as the migration case: copy `AgentConfig` → `vars.agent` and `StageAgentConfigs` → `stages.<stage>.vars.agent` per the spec, validate the bundle, and only then consider the source derived fields cleared. This path is reversible because the source data is the row's own `Variables` history.

## Open Questions

1. **Re-sync action for existing issues?** The snapshot means a project that changes its default model after creating issues must accept that those issues keep the old model. Do we want an explicit "re-merge variables" command, or is re-opening the issue sufficient? (Leaning: re-open is sufficient for now.)
2. **Migrate Settings UI routes off `GetAgentConfigAsync`?** `ConfigRoutes` / `OpencodeRoutes` still read agent config through the flat-dict methods. Should this issue consolidate them onto `GetVariables()`, or defer to keep the change tight? (Leaning: defer; `GetVariables` is the only path T1 needs.)
3. **`IssueInfo.AgentConfig` on create/update DTOs:** the `POST/PATCH /api/issues` path still accepts `agentConfig` / `stageModels` on the issue entity (`entities/issue/api/client.ts:21,29`). Is that path meant to write into `Variables` too, or does it stay a separate legacy surface? (Out of scope for #121 unless it interferes; it currently does not feed the runner path.)
4. **Embedded definition variables precedence vs. profile `Variables`:** confirm the intended order when a workflow YAML defines `vars` and the profile also has `vars` (profile should win). Verify with a spec.
