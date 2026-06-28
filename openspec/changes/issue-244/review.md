# Review Report

## Result: FAIL

## Repaired Items

- (none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/model/useWorkflowSessionFiltering.ts; packages/server/src/Mohist.Server/Sessions/Services/AgentSessionJsonHelper.cs; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: The status filter exposes the required `running`, `completed`, and `failed` options by force-adding them in `useWorkflowSessionFiltering.ts:90-93`, then filters by exact `session.status` equality in `useWorkflowSessionFiltering.ts:107-109`. The workflow-run sessions endpoint feeding this panel does not return those semantic statuses: `AgentSessionJsonHelper.StatusName` returns only `active` or `inactive` based on recent runtime activity (`AgentSessionJsonHelper.cs:11-16`), and `ToWorkflowDto` passes that value into the workflow session DTO (`AgentSessionQuerier.cs:560-577`). In the real candidate snapshot, selecting `completed`, `failed`, or `running` can therefore hide matching sessions instead of showing them, so the issue AC "列表支持按 status 筛选" and spec scenarios for `failed` / `completed` / `running` are not actually satisfied. [disallowed:reason] Repair would require deciding or changing the product/API status contract, not a local review fix.
  SuggestedAction: Make the workflow session DTO expose the same semantic status values the UI promises to filter by, or adjust the UI contract and filters to use the actual endpoint status values. Add an integration-style test using the real workflow-run session DTO shape, not only hand-authored client fixtures with `completed` and `failed` statuses.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (182 files, 2714 passed, 1 skipped), but the current tests use mocked statuses that do not match the server DTO contract.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/model/useWorkflowSessionFiltering.ts; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: Duration sort is specified to measure completed sessions to completion time and live sessions to current time, but the endpoint data used by `WorkflowSessionsPanel` cannot support that. `computeSessionDurationMs` only treats `completed`, `failed`, and `cancelled` as terminal (`useWorkflowSessionFiltering.ts:16-20`, `45-56`), while real workflow-run sessions are `active`/`inactive` as described in item-1. `ToWorkflowDto` also sends `CompletedAt` as `null` and `FailureReason` as `null` (`AgentSessionQuerier.cs:572-576`). As a result, inactive completed sessions are sorted as if they are still live (`now - startedAt`) instead of by actual runtime, violating the `createdAt/tokens/duration` sorting AC and the duration-sort spec. [disallowed:reason] Repair would require changing what the API exposes or how terminal state is derived.
  SuggestedAction: Expose reliable `completedAt` and terminal status/failure fields for workflow-run sessions, then update duration sorting and tests to cover the real active/inactive/current endpoint shape plus terminal completed/failed examples.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but the duration tests only use mocked terminal statuses and mocked `completedAt` values that the current endpoint does not provide.
  Status: unresolved

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx; packages/web/src/widgets/issue-workflow/model/useWorkflowSessionFiltering.ts
  Evidence: The stage filter renders all four executable stages, but disables any stage not already present in the current list (`WorkflowSessionsPanel.tsx:231-235`). The available stage set is derived only from currently visible session names (`useWorkflowSessionFiltering.ts:97-104`). This means a user cannot select an absent executable stage such as `check` to get an empty filtered result, even though the spec says the stage filter SHALL cover `plan`, `build`, `check`, and `integrate`, and clearing a filter should restore excluded sessions. The existing empty-result test drives a disabled option by programmatically firing a change event, which does not represent browser user behavior. [disallowed:reason] Repair changes filter UX semantics.
  SuggestedAction: Keep all four stage options enabled. Add a test that asserts the options are not disabled and that selecting an absent stage through user-level behavior produces the empty-result message.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but the passing test does not catch disabled options.
  Status: unresolved

- [ID: item-4]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/model/useWorkflowSessionFiltering.ts; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: Stage filtering is based on `session.sessionName` exactly matching `plan`, `build`, `check`, or `integrate` (`useWorkflowSessionFiltering.ts:22-27`, `107-112`). The server already has stage metadata labels available (`AgentSessionQuerier.cs:549-552`), but `WorkflowSessionDto` has no `Stage` field (`AgentSessionReadModels.cs:159-178`), so the UI cannot filter by the actual workflow stage. Any workflow session named after a work id, retry, recovery task, or custom session name will be omitted from its true stage filter even if it belongs to that stage. [disallowed:reason] Repair requires adding or changing a public DTO field and updating API/client contracts.
  SuggestedAction: Add `stage` to `WorkflowSessionDto` from the session metadata label and filter on that field, using `sessionName` only as a display/route identifier.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but tests only cover session names that equal stage names.
  Status: unresolved

- [ID: item-5]
  Severity: warning
  Scope: packages/web/src/pages/session/ui/SessionPage.tsx
  Evidence: `useSiblingSessions` supports locating the current session by `sessionName` and then by `id` (`useSiblingSessions.ts:32-37`), so legacy `/issues/:number/session/:sessionId` routes can still compute previous/next siblings. The sidebar highlight does not use the same fallback: it marks current only when `sibling.sessionName === currentKey` (`SessionPage.tsx:346-349`, `359-361`, with `currentKey` passed from `SessionPage.tsx:726-731`). On the still-registered legacy route (`App.tsx:61`), the sidebar can render without indicating the current session, violating the sidebar current-session indication scenario. [disallowed:reason] Repair changes route-visible product behavior.
  SuggestedAction: Use the same matcher for sidebar highlighting as the navigation hook, for example `sibling.sessionName === currentKey || sibling.id === currentKey`, and add a SessionPage test for the legacy session-id route.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but existing sidebar tests only use the workflow `sessionName` route.
  Status: unresolved

- [ID: item-6]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/WorkflowSessionsPanel.test.tsx
  Evidence: The responsive row acceptance criteria require no horizontal overflow and wrapping on narrow containers. Current tests assert Tailwind class names (`WorkflowSessionsPanel.test.tsx:234-353`) in jsdom, which does not perform layout and cannot verify actual overflow, wrapping, or legibility. This leaves the narrow-container AC only indirectly covered.
  SuggestedAction: Add a browser/layout test or visual check for a narrow workflow sessions panel with long session names, long model labels, metric chips, and failure text, asserting no horizontal scroll and that the key row content remains visible.
  Verification: `npm run test:run -w packages/web` passed, but jsdom class assertions do not exercise layout.
  Status: unresolved

## Follow-up Items

- [ID: item-7]
  Severity: follow-up
  Scope: openspec/changes/issue-244/tasks.json
  Evidence: The build appears implemented and verified, but all task `passes` flags remain `false` in `tasks.json` (`T-001` through `T-004`). This does not affect the product bundle directly, but it weakens traceability for humans comparing the post-build snapshot to planned work.
  SuggestedAction: If this artifact is intended to be updated after task completion, mark completed tasks consistently or document that `passes` is not maintained during Build.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- (none)

<promise>FAIL</promise>
