## Context

Issue #74 introduced per-stage model routing via `opencode.stageModels` in `config.jsonc` and the `setSessionConfigOption` ACP mechanism. All issues share the same stage-model mapping. This change adds per-issue model override, stored in the `issues` table and passed through the call chain as the highest-priority model source.

**Current state of #74 on this branch:** Not merged. `config-schema.ts` has `opencode.binPath` but no `stageModels`/`model`. `acp-session.ts` has no `setSessionConfigOption` call. This design accounts for both #74's infrastructure and #80's additions landing together.

**Key constraint:** The ACP session module (`acp-session.ts`) must NOT query the issues DB directly. Model values are read from the issue object at the call site and passed through options.

## Goals / Non-Goals

**Goals:**
- Store per-issue model preference in DB, expose via API
- Pass `issue.model` through `AcpSessionOptions` → `AcpConnectionOptions` → ACP `setSessionConfigOption` as highest priority
- Provide ModelSelector in Issue Detail Page for users to set/clear override
- `null` model = no override, fallback to stageModels/global/default

**Non-Goals:**
- Per-task or per-round model override (only per-issue)
- Affecting Explore sessions (they use their own model selection)
- Validating that the model string corresponds to a real configured provider (deferred to ACP runtime error)

## Decisions

### D1: Model stored as nullable TEXT column on issues table

Add `model TEXT` column (schema v15). `NULL` = no override. No new table needed — this is a simple attribute on the issue entity, like `priority`.

**Alternatives considered:**
- Separate `issue_config` table: Over-engineered for a single field. Revisit if per-issue config grows.

### D2: Pass-through architecture (no DB coupling in acp-session)

`AcpSessionOptions` and `AcpConnectionOptions` gain `model?: string`. The caller (workflow-controller, ralph-executor, API start/reopen/approve/reject) reads `issue.model` and passes it. The ACP session module never imports `IssueRepo`.

This follows the established pattern: `issueId`, `projectId`, `issueNumber` are already passed through options rather than looked up internally.

**Alternatives considered:**
- ACP session queries IssueRepo directly: Couples the session layer to the DB layer. Breaks testability and separation of concerns.

### D3: Model format validation at API boundary only

`PATCH /api/issues/:number` validates that `model` (when present and not null) contains a `/` (i.e., `"provider/model-id"` format). The ACP session layer does not re-validate — if an invalid model reaches ACP, it will produce a runtime error from opencode.

**Alternatives considered:**
- No validation: Errors manifest late and are confusing.
- Full provider/model existence check: Requires loading config + models-dev at API time, over-engineered.

### D4: ModelSelector wrapper for Issue Detail Page

The existing `ModelSelector` component calls `api.updateSessionModel(sessionId, ...)` for Explore. For Issue Detail, we create a thin wrapper or a separate component that:
1. Uses the same visual UI (fuzzy search, recent models, grouped list)
2. Calls `PATCH /api/issues/:number` with `{ model }` instead of `api.updateSessionModel`
3. Supports a "Use default" option to clear the override (`{ model: null }`)

This avoids refactoring the existing `ModelSelector` props interface.

**Alternatives considered:**
- Refactor `ModelSelector` to accept a generic `onSelect` callback: Clean but risky — breaks Explore page if done carelessly. Wrapper is safer.

### D5: RalphExecutorContext gains optional `model` field

`RalphExecutorContext` gets `model?: string`. The `_acpSessionRunner` call inside `runRalphLoop` passes `context.model` in its options. This is populated by the workflow controller's build stage, which reads `issue.model`.

## Risks / Trade-offs

- **[#74 not merged yet]** → This change depends on #74's `setSessionConfigOption` mechanism in `acp-session.ts`. Both changes must land together. The model resolution code in acp-session is written once for the combined state.
- **[Invalid model string stored]** → Only format validation (`contains /`) at API level. If user stores `"nonexistent/model"`, the next ACP session will fail. Mitigated by: ModelSelector only shows configured models; manual API users get clear format error.
- **[Model change mid-pipeline]** → Documented as expected: change takes effect on next ACP session, not current. Each ralph task spawns a new ACP session, so model changes between tasks work naturally.

## Migration Plan

1. Add `migrateToVersion15` in `migrations.ts`: `ALTER TABLE issues ADD COLUMN model TEXT`
2. Update `IssueRow` interface and `rowToIssue` in `issue-repo.ts` to include `model`
3. Add `updateModel(id, model)` method to `IssueRepo`
4. Add `model` to the generic `update()` method's data type
5. No data migration needed — existing rows get `NULL` (correct default)

## Open Questions

None — design is straightforward. The only open question was whether to use pass-through vs DB coupling, resolved in D2.
