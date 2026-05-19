# Review Report

## Result: FAIL

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/cli/web/src/components/SessionPage.tsx`
  Evidence: The active session page still renders transcript rows from `detail` instead of the live-updated `turns` state returned by `useSessionTranscript`. `useSessionTranscript(...)` is called at `packages/cli/web/src/components/SessionPage.tsx:326-341`, but the page immediately computes `displayTurns` from `detail` at `packages/cli/web/src/components/SessionPage.tsx:346` and passes those stale replay turns into `SessionTranscriptLayout` at `packages/cli/web/src/components/SessionPage.tsx:526-534`. This breaks the spec requirement that live and replay transcript rendering stay semantically equivalent after late `tool_call_update` events, because the live page is not actually rendering the live transcript state. It also leaves the acceptance criterion "When a `tool_call_update` provides a more specific title, target, raw input, output, or metadata, the final rendered tool row uses the updated semantic data" unproven in the active route. Existing tests pass because they exercise `useSessionTranscript` in isolation and static `detail.turns` rendering separately, but they do not verify that `SessionPage` wires the live hook result into the rendered transcript. [disallowed:product-behavior-change]
  SuggestedAction: Project the rendered transcript from the current `turns` state, not `detail.turns`, while preserving the persisted replay path for non-live sessions. Add a route-level test that drives a live transcript update through `SessionPage` and asserts the rendered row changes after a late tool update.
  Verification: `cd packages/cli && npm test -- --run tests/session-transcript-service.test.ts web/tests/SessionPage.transcript.test.tsx && npm run build`
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/cli/src/services/session-transcript-service.ts`; `packages/cli/web/src/lib/session-transcript-display.ts`
  Evidence: Todo tools are still suppressed in the active transcript pipeline. Backend normalization still classifies `todowrite` and `todo` as internal tools via `INTERNAL_TOOL_NAMES` at `packages/cli/src/services/session-transcript-service.ts:216-219` and marks them hidden when creating/updating tool parts at `packages/cli/src/services/session-transcript-service.ts:1718-1722` and `packages/cli/src/services/session-transcript-service.ts:1878-1882`. The active display projection then drops them again unconditionally with `isInternalTool(normalizedName)` at `packages/cli/web/src/lib/session-transcript-display.ts:120-128` and `packages/cli/web/src/lib/session-transcript-display.ts:354-358`. This directly violates the modified requirements in `specs/agent-session-ui/spec.md:18-21` and the acceptance criterion "`todowrite`/`todo` rows are not silently suppressed when they contain todos; they show item count and todo statuses in a compact view." Backend tests still assert the old hidden behavior at `packages/cli/tests/session-transcript-service.test.ts:1375-1409`, so the current test suite is reinforcing the wrong contract. [disallowed:product-behavior-change]
  SuggestedAction: Stop marking todo tools hidden when they contain user-visible todo data, remove the projection-level filter for these families, and update backend/frontend tests to assert compact todo rendering in the active transcript route.
  Verification: `cd packages/cli && npm test -- --run tests/session-transcript-service.test.ts web/tests/SessionPage.transcript.test.tsx && npm run build`
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/cli/web/tests/SessionPage.transcript.test.tsx`
  Evidence: The active transcript-page test file covers static semantic rendering for read/write/edit/apply_patch/bash/webfetch/context grouping, but it does not cover the required `todowrite`/`todo`, `task`, `question`, `websearch`, or live late-update wiring through `SessionPage` itself. That gap allowed both blockers above to pass the suite.
  SuggestedAction: Add route-level tests for todo visibility, delegation/network renderers, and at least one live `tool_call_update` path that updates a row after first render.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: workspace state
  Evidence: The worktree contains unrelated changes outside this review target: `.opencode/package-lock.json` is modified.
  SuggestedAction: Keep unrelated workspace changes separate from this issue's fix and review them independently.
  Status: out-of-scope

- [ID: item-5]
  Severity: warning
  Scope: `packages/cli` dependency audit
  Evidence: `npm run build` completed successfully, but `npm --prefix web install` reported 3 vulnerabilities (2 moderate, 1 high). This did not block the current transcript change review.
  SuggestedAction: Review `npm audit` output in a separate dependency-maintenance task.
  Status: out-of-scope

<promise>FAIL</promise>
