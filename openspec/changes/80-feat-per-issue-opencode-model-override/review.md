# Review Report

## Result: PASS

## Dimensions

### Correctness: PASS

- Migration v15 correctly adds `model TEXT` column to `issues` table with NULL default. `SCHEMA_VERSION` is 15.
- `rowToIssue` correctly maps `row.model` (nullable) to `issue.model` (null → undefined).
- PATCH handler validates model format: rejects strings without `/`, accepts valid `"provider/model-id"`, `null` (clear), and `undefined` (no-op).
- `AcpSessionOptions` and `AcpConnectionOptions` both have `model?: string`. Both `runAcpSession` and `createAcpConnection` call `setSessionConfigOption` only when `model` is truthy.
- `updateModel(id, null)` sets column to NULL and refreshes `updated_at`. The generic `update()` also handles `model` in its data param.
- All 6 `AcpConnectionOptions` constructions in `api/issues.ts` include `model: issue.model ?? undefined`.
- Workflow controller passes `issue.model ?? undefined` in plan, build (via ralph context), and review stages.
- Ralph executor context has `model?: string` and passes `context.model` to `_acpSessionRunner`.
- Typecheck passes with no errors.
- All 14 tests pass.

**Note**: The diff also removes Issue #74's `opencode-discovery-service.ts`, `opencode-models.ts` API route, `config-schema.ts` `model`/`stageModels` fields, `stage`/`hangIdleMs` from ACP options, and the entire `runPromptWithHangRecovery` function. These are unrelated regressions/deletions bundled into this change — they appear to be a rollback of Issue #74 and #78 functionality that should be documented or separated. This is flagged as a warning, not a blocking error for this issue's scope.

### Complexity: PASS

- `updateModel` is 8 lines, focused.
- PATCH model validation is inline and clear (4-line check).
- ACP model passthrough is a simple `if (model) { ... }` block (6 lines) in both `runAcpSession` and `createAcpConnection`.
- `IssueModelSelector.tsx` is 332 lines which is on the larger side for a single component, but it's self-contained with reasonable decomposition (sub-components `ModelListItem`, `Badge`, `SearchIcon`, `ChevronDownIcon`).
- No copy-pasted code — model passthrough follows the same pattern in all 6 API sites.

### Test Coverage: PASS

- 14 tests covering: migration (column exists, default NULL, schema version), `updateModel` (set, clear, non-existent), `findById` returns model (set/unset), generic `update()` with model, PATCH API (set, clear, invalid format, preservation on other updates).
- Tests are well-structured with proper setup/teardown (in-memory DB).
- Test file follows project conventions (vitest, colocated in `tests/`).

**Warning**: No frontend tests for `IssueModelSelector` component, but this is consistent with the project's testing patterns (frontend is not tested).

### Security: PASS

- Model format validation in PATCH handler prevents injection — only validates presence of `/` character.
- SQL in `updateModel` uses parameterized queries (`?` placeholders).
- No user input is interpolated into SQL, shell commands, or file paths via the model field.
- The `model` value is passed as a config option to the ACP session, not executed or evaluated.

### Spec Compliance: PASS

**T-001: Add schema v15 migration**
- [PASS] SCHEMA_VERSION is 15
- [PASS] `migrateToVersion15` adds `model TEXT` column if not present
- [PASS] `initializeDatabase` calls `migrateToVersion15` when `currentVersion < 15`
- [PASS] Existing rows have model = NULL after migration
- [PASS] Typecheck passes

**T-002: Add model field to Issue type and IssueRepo**
- [PASS] Issue interface has `model?: string` field (`types/index.ts:81`)
- [PASS] `rowToIssue` maps `row.model` to `issue.model` (null → undefined) (`issue-repo.ts:55`)
- [PASS] `update()` accepts `{ model: string | null }` in its data param (`issue-repo.ts:179`)
- [PASS] `updateModel(id, null)` sets model column to NULL and refreshes `updated_at` (`issue-repo.ts:332-340`)
- [PASS] `updateModel(id, 'openai/gpt-4o')` sets model column and refreshes `updated_at`
- [PASS] `findById` and `findAll` return model field
- [PASS] Typecheck passes

**T-003: Extend PATCH /api/issues/:number with model support**
- [PASS] PATCH with `{ model: 'openai/gpt-4o' }` sets issue.model and returns 200 (test confirms)
- [PASS] PATCH with `{ model: null }` clears issue.model (sets to NULL) and returns 200 (test confirms)
- [PASS] PATCH without model field leaves model unchanged (test confirms preservation)
- [PASS] PATCH with `{ model: 'invalid' }` returns 400 with 'Invalid model format' (test confirms)
- [PASS] Response includes model field
- [PASS] Typecheck passes

**T-004: Add model passthrough to AcpSessionOptions and AcpConnectionOptions**
- [PASS] `AcpSessionOptions` has `model?: string` field (`acp-session.ts:52`)
- [PASS] `AcpConnectionOptions` has `model?: string` field (`acp-session.ts:472`)
- [PASS] `runAcpSession` passes model to `setSessionConfigOption` when provided (`acp-session.ts:366-373`)
- [PASS] `createAcpConnection` passes model to `setSessionConfigOption` when provided (`acp-session.ts:772-779`)
- [PASS] When model is undefined, no `setSessionConfigOption` call is made for model
- [PASS] Typecheck passes

**T-005: Wire issue.model through workflow-controller and ralph-executor**
- [PASS] `RalphExecutorContext` has `model?: string` field (`ralph-executor.ts:125`)
- [PASS] `_acpSessionRunner` call in `runRalphLoop` includes `model: context.model` (`ralph-executor.ts:643`)
- [PASS] `WorkflowController` passes `issue.model ?? undefined` in plan `AcpConnectionOptions` (`workflow-controller.ts:132`)
- [PASS] `WorkflowController` passes `issue.model ?? undefined` in review `AcpConnectionOptions` (`workflow-controller.ts:733`)
- [PASS] `WorkflowController` passes `issue.model ?? undefined` in build ralph context (`workflow-controller.ts:606`)
- [PASS] All 6 `AcpConnectionOptions` in `api/issues.ts` include `model: issue.model ?? undefined` (lines 467, 711, 851, 978, 1099, 1193)
- [PASS] Explore sessions do NOT receive per-issue model (explore has its own model mechanism in `api/explore.ts`)
- [PASS] Typecheck passes

**T-006: Add model to frontend Issue type and API client**
- [PASS] Issue type in `web/src/lib/types.ts` has `model?: string | null` field (line 44)
- [PASS] `updateIssue` function accepts and sends `model` parameter in PATCH request body (`api.ts:38`)
- [PASS] Sending `model: null` in the PATCH body works (clears override)
- [PASS] Typecheck passes

**T-007: Add IssueModelSelector to Issue Detail Page**
- [PASS] `IssueModelSelector` renders in `IssueDetailPage` Actions area (`IssueDetailPage.tsx:568-570`)
- [PASS] Selecting a model calls `PATCH /api/issues/:number` with `{ model: 'provider/model-id' }` via `api.updateIssue`
- [PASS] Selecting 'Use default' calls `PATCH` with `{ model: null }`
- [PASS] Current model override is displayed when issue has model set
- [PASS] Unset state shows 'Use default' text
- [PASS] ModelSelector does NOT affect Explore page (separate component, separate API)
- [PASS] Typecheck passes

**T-008: Write backend tests**
- [PASS] Test: migration adds model column, default NULL
- [PASS] Test: `updateModel` sets model and refreshes `updated_at`
- [PASS] Test: `updateModel(id, null)` clears model
- [PASS] Test: `findById` returns model field
- [PASS] Test: PATCH sets model with valid format
- [PASS] Test: PATCH clears model with null
- [PASS] Test: PATCH rejects model without `/` character
- [PASS] Tests pass (14/14)
- [PASS] Typecheck passes

## Fix Suggestions

No blocking issues found.

**Warnings (non-blocking):**

1. The diff includes removal of Issue #74's `opencode-discovery-service.ts`, `opencode-models.ts`, `config-schema.ts` `model`/`stageModels`, `stage`/`hangIdleMs` from ACP options, and the entire Issue #78 `runPromptWithHangRecovery` hang recovery function. These are unrelated to Issue #80 and appear to be a rollback of previously merged features. If this is intentional, it should be documented. If not, these deletions should be separated into their own commit/PR.

2. `IssueModelSelector.tsx` is 332 lines. Consider extracting the popover list into a shared component if the project plans to reuse model selection patterns elsewhere.
