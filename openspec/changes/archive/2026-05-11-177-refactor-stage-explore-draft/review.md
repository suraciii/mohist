# Review: 177-refactor-stage-explore-draft

## Summary

Removes deprecated `Draft` and `Explore` from the pipeline `Stage` enum, unifies `STAGE_ORDER` and `STAGE_TRANSITIONS` around the backlog-first model (`backlog -> plan -> build -> check -> integrate -> done`), removes `explore` from the default workflow, and aligns frontend stage definitions. The core type system and model changes are correct and complete.

## Correctness

### Stage enum — PASS
`packages/cli/src/types/index.ts:1-8` — Enum is `Backlog, Plan, Build, Check, Integrate, Done`. No `Draft` or `Explore`.

### STAGE_ORDER — PASS
`packages/cli/src/types/index.ts:10-17` — Order is `backlog -> plan -> build -> check -> integrate -> done`.

### STAGE_TRANSITIONS — PASS
`packages/cli/src/types/index.ts:19-26` — Transitions:
- `Backlog -> [Plan]`
- `Plan -> [Build]`
- `Build -> [Check]`
- `Check -> [Integrate, Build]` (recovery loop)
- `Integrate -> [Done, Build]` (recovery loop)
- `Done -> []`

Matches design D3. `Check -> Integrate` is the primary path; `Check -> Build` and `Integrate -> Build` are explicit recovery loops. No Draft/Explore transitions remain.

### Default workflow — PASS
`packages/cli/src/workflow/workflow-loader.ts:36-66` — `DEFAULT_WORKFLOW.stages` lists `plan, build, check, integrate, done`. No `explore` or `draft`.

### Frontend Stage — PASS
`packages/cli/web/src/lib/types.ts:1-8` — Frontend `Stage` enum matches backend: `Backlog, Plan, Build, Check, Integrate, Done`. `STAGE_ORDER:10-17` includes all 6. No `Draft` or `Explore`.

### Kanban grouping — PASS
`packages/cli/web/src/lib/kanban-grouping.ts:10-17` — STAGES array includes all 6 stages from enum, no Draft/Explore column.

### Workflow engine start behavior — PASS
`packages/cli/src/workflow/workflow-engine.ts:134-138` — When `issue.stage === Stage.Backlog`, transitions to `Stage.Plan`.

### Default issue creation — PASS
`packages/cli/src/db/issue-repo.ts:114` — New issues created with `Stage.Backlog`.

### EXECUTABLE_MODEL_STAGES — PASS
`packages/cli/src/config/model-resolution.ts:8-15` — Lists `backlog, plan, build, check, integrate, done`. No `explore` or `draft`.

## Warnings

### W1: Stale "draft" string in CLI color map
**File:** `packages/cli/src/cli/commands/issue.ts:29`
```typescript
const colors: Record<string, typeof chalk.green> = {
    draft: chalk.gray,
    plan: chalk.blue,
    ...
};
```
The key `draft` is dead — no issue will ever have stage `"draft"` after this change. It falls through to the default `chalk.white` for `"backlog"` stages. Should be changed to `backlog: chalk.gray`.

### W2: Stale "draft" in API response messages (4 locations)
**File:** `packages/cli/src/api/issues.ts`
- Line 2876: `"reset to draft stage"` → should be `"reset to Backlog stage"`
- Line 2927: `"reset to draft stage"` → should be `"reset to Backlog stage"`
- Line 3081: `"reset to draft"` → should be `"reset to Backlog"`
- Line 3130: `"reset to draft"` → should be `"reset to Backlog"`

The code correctly uses `Stage.Backlog` / `transitionToStage(issue.id, Stage.Backlog)` — only the user-facing string is stale.

### W3: Stale "Draft" in tool description
**File:** `packages/cli/src/tools/update-issue-tool.ts:16`
```typescript
description: 'Update the draft issue linked to this explore session. Only available when the linked issue is still in Draft stage.'
```
The runtime check correctly uses `Stage.Backlog` (line 23), but the description still says "Draft stage". Should say "Backlog stage".

### W4: Stale "Draft" in explore agent prompt (2 locations)
**File:** `packages/cli/src/agents/explore-agent.ts`
- Line 151: `"The issue will remain in Draft stage until it is promoted through the workflow"` → should be `"Backlog stage"`
- Line 156: `"The issue is no longer in Draft, so it cannot be updated from here."` → should be `"no longer in Backlog"`

### W5: Tests assert stale "draft" string in API messages
**File:** `packages/cli/tests/api-routes.test.ts`
- Line 1009: `expect(response.body.data.message).toContain('reset to draft')`
- Line 1064: `expect(response.body.data.message).toContain('reset to draft')`

These tests correctly verify the API response but will need updating when W2 is fixed. They currently pass because the source strings still say "draft".

### W6: Missing `integrate` in REBASE_ALLOWED_STAGES (agent-runner-service)
**File:** `packages/cli/src/services/agent-runner-service.ts:32`
```typescript
const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Done];
```
The API-level constant at `packages/cli/src/api/issues.ts:2750` includes `Stage.Integrate`:
```typescript
const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate, Stage.Done];
```
The agent-runner-service version omits `Integrate`. This is a pre-existing inconsistency, not introduced by this PR, but worth noting as it could block rebase during the Integrate stage.

### W7: Hardcoded stage strings in SessionTimeline
**File:** `packages/cli/web/src/components/SessionTimeline.tsx:133-142`
`PIPELINE_STAGES` uses hardcoded string literals (`'plan'`, `'build'`, etc.) instead of the `Stage` enum. The `stageOrder` array at line 142 also uses hardcoded strings. While functionally correct, this duplicates the canonical stage list and will drift if stages change. Low priority — not a spec violation.

## Security

No issues found. No secrets exposed, no injection vectors introduced.

## Complexity

All functions under review are within reasonable complexity bounds. The enum and constant definitions are straightforward. No concerns.

## Test Coverage

All 111 test files pass (2024 tests, 6 skipped). No `Stage.Draft` or `Stage.Explore` references remain in test stage assertions. The `model-override-regression.test.ts` correctly tests that `EXECUTABLE_MODEL_STAGES` does not contain `'explore'` or `'draft'` while still allowing arbitrary keys in `stageModels`.

## Spec Compliance

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Backend Stage enum no longer includes Draft/Explore | **PASS** | `types/index.ts:1-8` — enum is `Backlog, Plan, Build, Check, Integrate, Done` |
| Frontend Stage enum / STAGE_ORDER matches backend | **PASS** | `web/src/lib/types.ts:1-17` — identical values and order |
| Default workflow.yaml no longer declares explore stage | **PASS** | `workflow-loader.ts:36-66` — stages are `plan, build, check, integrate, done` |
| Issue creation still enters Backlog | **PASS** | `db/issue-repo.ts:114` — default is `Stage.Backlog` |
| Start pipeline still advances Backlog → Plan | **PASS** | `workflow-engine.ts:134-138` — `Stage.Backlog` → `Stage.Plan` |
| Check approval still advances to Integrate, then Done | **PASS** | `STAGE_TRANSITIONS[Check]` includes `Integrate`; `STAGE_TRANSITIONS[Integrate]` includes `Done` |
| Explore pages/session/API still work, not as pipeline stage | **PASS** | All Explore-domain code (services, routes, repo, web components) preserved; no `Stage.Explore` references in pipeline logic |
| No test needs Draft/Explore as legal pipeline stage | **PASS** | No `Stage.Draft` or `Stage.Explore` references in stage assertions; tests use `Stage.Backlog` |

### Pipeline model spec (pipeline-model/spec.md)

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Draft and Explore not accepted as legal pipeline stages | **PASS** | Not in enum, order, or transitions |
| Stage order matches real pipeline (`backlog -> ... -> done`) | **PASS** | `STAGE_ORDER` at `types/index.ts:10-17` |
| Start enters plan from backlog | **PASS** | `workflow-engine.ts:134-138` |
| Check approval advances to integrate, not skip to done | **PASS** | `STAGE_TRANSITIONS[Check]` = `[Integrate, Build]` |
| Recovery loops use real stages, no Draft/Explore dependency | **PASS** | `Check→Build`, `Integrate→Build` are the only non-linear transitions |

### Web UI spec (web-ui/spec.md)

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| UI shows backlog→plan→build→check→integrate→done | **PASS** | `kanban-grouping.ts:10-17` STAGES array; `PipelineView.tsx:20-21`; `types.ts:10-17` |
| No draft/explore as pipeline stages | **PASS** | No `Draft` or `Explore` in frontend Stage enum |
| Explore surfaces work without Stage.Explore | **PASS** | Explore pages, sessions, hooks preserved; no pipeline dependency |

### Workflow config spec (workflow-config/spec.md)

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Backlog remains lifecycle start state | **PASS** | Issues created with `Stage.Backlog`; not in `DEFAULT_WORKFLOW.stages` but in `STAGE_ORDER` |
| Validation rejects draft/explore | **PASS** | Not in enum; no workflow declaration; `EXECUTABLE_MODEL_STAGES` excludes them |

### Workflow definition spec (workflow-definition/spec.md)

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Default runnable stages don't include explore | **PASS** | `DEFAULT_WORKFLOW.stages` = `plan, build, check, integrate, done` |
| Default runnable stage list matches execution order | **PASS** | Stages listed in `plan→build→check→integrate→done` order |

## Overall Verdict

The core refactoring is **correct and complete**. All acceptance criteria pass. The Stage enum, STAGE_ORDER, STAGE_TRANSITIONS, default workflow, and frontend definitions are aligned on the `backlog -> plan -> build -> check -> integrate -> done` model with no `Draft` or `Explore` pipeline references.

The warnings (W1–W7) are stale string literals in user-facing messages, tool descriptions, agent prompts, and a color map key — none affect runtime correctness because the logic already uses `Stage.Backlog` exclusively. These are cosmetic/internsistency issues that should be fixed in a follow-up but don't block this change.

<promise>PASS</promise>