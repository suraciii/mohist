# Self Review Report

## Result: PASS

Cross-checked the issue body (改动 1–4 + acceptance criteria) against `proposal.md`, `specs/`, `design.md`, and `tasks.json`, and verified factual claims against the actual codebase (`IssueWorkflowProfileRow.cs`, `ProjectWorkflowProfileRow.cs`, `IIssueWorkflowProfile.cs`, `IssueWorkflowProfileManager.cs`, `ConfigService.cs`, `IssueGrain.cs`, `WorkflowProfileManager.cs`, `IssueQuerier.cs`, `entities/issue/api/client.ts`).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal's "What Changes" and Impact sections inherited the issue body's premise that `IssueWorkflowProfile` has `AgentConfig` / `StageAgentConfigs` storage fields, that `IssueGrain.HydrateAsync` exists, that `IssueWorkflowProfileManager.GetAgentConfigAsync/UpdateAgentConfigAsync` exist, and that the frontend file `entities/issue-workflow-profile/api/queries.ts` exists. Verified against code: none of these are true — both profile rows already store only a `Variables` (`VariableBundle`) column; the merge point is `IssueGrain.StartWorkflowAsync`/`BuildIssueVariables`; the manager already has `GetVariablesAsync`/`PatchVariablesAsync`; and the web client at `entities/issue/api/client.ts` already writes `vars.agent`/`stages.X.vars.agent` to the already-generic `workflow-profile/variables` endpoint. The `design.md` already documented these findings; the proposal had not been aligned to them.
  Verification: Edited `proposal.md` — fixed the T1 reference to `IssueGrain.StartWorkflowAsync`; rewrote the BREAKING change as interface-level (dead `IIssueWorkflowProfile` agent methods) with an explicit note that no DB-field removal is needed; replaced the inaccurate frontend "switches payload" bullet with a "no frontend/API change required" statement; corrected the Impact section to reference real files/methods and the no-op migration. Re-read the full proposal; it is now consistent with `design.md` and the task targets.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: `tasks.json` T-003 implements two spec requirements — the workflow-config "Issue creation merges…" requirement (referenced in its `spec` field) AND the workflow-engine MODIFIED "Workflow uses issue-aware model resolution" requirement (covered by its acceptance criteria for direct runtime read + stage dispatch via ordinary variable lookups + recovery-session reuse), but only the workflow-config requirement was referenced, leaving the workflow-engine spec without an explicit task trace.
  Verification: Added an explicit reference to `specs/workflow-engine/spec.md#workflow-uses-issue-aware-model-resolution` in T-003's notes, mapping its acceptance criteria to that requirement. Confirmed `tasks.json` remains valid JSON and the DAG is unchanged.
  Status: resolved

## Blocking Items

(None.)

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-003 collapses `WorkflowProfileManager.LoadVariablesAsync` from a project+issue runtime merge to an issue-only direct read. This is a behaviour change for ALL variables (not just `agent`) and relies on the T1 snapshot capturing the project layer. `design.md` documents this as Decision 4 with a dedicated risk and mitigation (update `VariableScopeSpecs` / `IssueVariableBuilderSpecs`). The feasibility is sound given the snapshot semantics, but it is the highest-risk change in the plan.
  SuggestedAction: During T-003 implementation, confirm via the updated `VariableScopeSpecs` that no non-agent variable depended on the live runtime project merge, and verify `ResolvedTemplate.EmbeddedVariables` still apply at the runner boundary (already listed as a T-003 acceptance criterion).
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue's 改动 4 explicitly expects a frontend API payload change. The plan (per `design.md`) treats the in-scope `workflow-profile/variables` path as already generic and scopes the separate `POST/PATCH /api/issues` issue-entity `model`/`stageModels` fields as a non-goal. This is a deliberate, documented narrowing, but it diverges from the literal issue text.
  SuggestedAction: Confirm with the issue author that the `workflow-profile/variables` endpoint is the intended surface (the issue's `workflow-config` endpoint name does not exist in the codebase) and that the issue-entity API path is intentionally out of scope. No change needed if confirmed.
  Status: follow-up

## Checks Performed

- Alignment: every "What Changes" entry traces to an issue 改动/acceptance criterion; all issue acceptance criteria (形态统一, 合并逻辑, BuildVariables, 用户感知, Tests, 数据迁移) are covered by specs and tasks.
- Completeness: all 5 spec requirements (4 ADDED in workflow-config, 1 MODIFIED in workflow-engine) map to tasks; every requirement has ≥1 scenario (12 + 5 scenarios); edge cases (snapshot semantics, embedded vars, test-double breakage, defensive migration) are covered.
- Consistency: proposal Capabilities (workflow-config + workflow-engine, both modified, no new capabilities) match the spec files; spec delta operations are correct (ADDED for new workflow-config requirements, MODIFIED with full content for the reframed workflow-engine requirement); all 4 task `spec` anchors exactly match requirement headers; design decisions map to tasks.
- Feasibility: 4 tasks at appropriate granularity (complete feature slices; no "define interface / register DI / move file / standalone test" over-splits); each carries its own test coverage.
- Dependencies: priorities [1,2,3,4]; every `dependsOn` references an existing ID with strictly lower priority; no cycles (DAG verified programmatically).

<promise>PASS</promise>
