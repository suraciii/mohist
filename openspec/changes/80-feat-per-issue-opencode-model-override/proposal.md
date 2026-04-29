## Why

Issue #74 implemented per-stage model routing (`opencode.stageModels`), but all issues share the same global mapping. In practice, issue complexity varies widely — a trivial bugfix shouldn't consume the same expensive model as a major refactor. Users need per-issue model override to control cost and speed per issue.

## What Changes

- DB: `issues` table gains `model TEXT` column (nullable, `NULL` = no override)
- IssueRepo: `findById` / `findAll` return `model` field; new `updateModel(id, model: string | null)` method
- API: `PATCH /api/issues/:number` accepts `{ model: "provider/model-id" }` or `{ model: null }` (clear override)
- ACP session: model resolution adds per-issue `model` as highest priority, falling through to stageModels → global → built-in default
- Workflow controller: reads `issue.model` and passes it through `AcpSessionOptions.model` to the ACP session runner
- Frontend: Issue type gains `model?: string`; Issue Detail Page adds ModelSelector in Actions area, calling PATCH to persist

## Capabilities

### New Capabilities

- `per-issue-model-override`: DB storage, API, and ACP priority chain for per-issue coder model override

### Modified Capabilities

- `local-issue-store`: issues table schema adds `model` column, IssueRepo gains `updateModel`
- `http-api`: `PATCH /api/issues/:number` accepts `model` field
- `agent-runtime`: model resolution priority chain extended with per-issue override as top priority
- `web-ui`: Issue Detail Page shows ModelSelector for per-issue model control

## Impact

- `packages/cli/src/db/migrations.ts` — new migration for `model` column
- `packages/cli/src/db/issue-repo.ts` — `model` field read/write
- `packages/cli/src/api/issues.ts` — PATCH handler extended
- `packages/cli/src/agent-runtime/acp-session.ts` — model priority chain (1-line change)
- `packages/cli/src/workflow/workflow-controller.ts` — read issue.model, pass to AcpSessionOptions
- `packages/cli/src/openspec/ralph-executor.ts` — context gains model field, pass-through
- `packages/cli/web/src/lib/types.ts` — Issue type add model
- `packages/cli/web/src/lib/api.ts` — updateIssue support model
- `packages/cli/web/src/components/IssueDetailPage.tsx` — add ModelSelector
