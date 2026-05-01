## ADDED Requirements

### Requirement: Toast notification provider

WebUI SHALL use `sonner` as the toast notification library. The `<Toaster />` component SHALL be mounted once at the app root level in `App.tsx`, inside the `BrowserRouter` but outside route content.

#### Scenario: Toaster mounted at app root
- **WHEN** the WebUI application renders
- **THEN** the `<Toaster />` component from sonner is present in the component tree
- **AND** toast notifications are visible from any page

### Requirement: Toast trigger API

WebUI SHALL export a `useToast` hook (or equivalent) that provides typed toast functions: `toast.success(message)`, `toast.error(message)`, `toast.info(message)`, `toast.loading(message)`. Each function SHALL accept an optional `description` string for additional detail.

#### Scenario: Triggering a success toast
- **WHEN** code calls `toast.success("Issue created", { description: "Issue #47 has been created" })`
- **THEN** a success-styled toast appears in the default toast position
- **AND** the toast auto-dismisses after the default sonner duration

#### Scenario: Triggering an error toast
- **WHEN** code calls `toast.error("Failed to save", { description: "Network error" })`
- **THEN** an error-styled toast appears
- **AND** the toast has a longer duration than success toasts (or is dismissible manually)

### Requirement: Mutation success toasts

WebUI mutation hooks SHALL show a success toast when a user-initiated mutation completes successfully. The following mutations SHALL trigger toasts:

| Mutation Hook | Toast Message |
|---|---|
| `useCreateProject` | "Project created" |
| `useDeleteProject` | "Project deleted" |
| `useUseProject` | "Switched to project" |
| `useSendMessage` | "Message sent" |
| `useUnarchiveIssue` | "Issue unarchived" |
| `useSaveProvider` | "Provider saved" |
| `useDeleteProvider` | "Provider deleted" |
| `useRebuildSystem` | "Rebuild started" |
| `useUpdateConfig` | "Setting updated" |
| `useUpdateOpencodeModel` | "Model updated" |
| `useSetModel` | "Model updated" |
| `useSetOpencodeModelConfig` | "Model updated" |
| `useSetLogLevel` | "Log level updated" |
| `useSetAgentRuntime` | "Agent runtime updated" |
| `useSetStageModels` | "Stage models updated" |
| `useCreateExploreSession` | "Explore session created" |
| `useUpdateExploreSessionTitle` | "Title updated" |
| `useTestProvider` | "Provider test passed" |

#### Scenario: Create project succeeds
- **WHEN** `useCreateProject` mutation succeeds
- **THEN** a success toast with message "Project created" is displayed

#### Scenario: Delete project succeeds
- **WHEN** `useDeleteProject` mutation succeeds
- **THEN** a success toast with message "Project deleted" is displayed

#### Scenario: Test provider succeeds
- **WHEN** `useTestProvider` mutation succeeds
- **THEN** a success toast with message "Provider test passed" is displayed

#### Scenario: Mutation succeeds with no toast for read-only queries
- **WHEN** a `useQuery` hook (non-mutation) completes
- **THEN** no toast is displayed

### Requirement: Mutation error toasts

WebUI mutation hooks SHALL show an error toast when a mutation fails. The toast SHALL display the error message from the API response.

#### Scenario: Mutation fails with server error
- **WHEN** a mutation (e.g., `useCreateProject`) fails
- **THEN** an error toast is displayed with the error message from the API response

#### Scenario: Mutation fails with network error
- **WHEN** a mutation fails due to a network error
- **THEN** an error toast is displayed with a generic "Request failed" message

### Requirement: SSE background event toasts

WebUI SHALL display toast notifications for key SSE background events. These toasts SHALL only fire when the event does NOT pertain to the currently viewed issue (to avoid duplicate UI feedback).

| SSE Event | Toast Type | Message |
|---|---|---|
| `agent_paused` | info | "Issue #N needs approval" |
| `agent_error` | error | "Issue #N encountered an error" |
| `rebase_conflict` (status=failed) | error | "Rebase conflict on Issue #N" |
| `merge_completed` | success | "Issue #N merged successfully" |
| `merge_failed` | error | "Merge failed for Issue #N" |

#### Scenario: Agent pauses on a different issue
- **WHEN** SSE receives `agent_paused` event for issue #12
- **AND** user is currently viewing issue #47
- **THEN** an info toast with message "Issue #12 needs approval" is displayed

#### Scenario: Agent pauses on the current issue
- **WHEN** SSE receives `agent_paused` event for issue #47
- **AND** user is currently viewing issue #47
- **THEN** no toast is displayed (the approval panel already shows inline)

#### Scenario: Merge completes on a background issue
- **WHEN** SSE receives `merge_completed` event for issue #12
- **THEN** a success toast with message "Issue #12 merged successfully" is displayed

#### Scenario: Merge fails on a background issue
- **WHEN** SSE receives `merge_failed` event for issue #12
- **THEN** an error toast with message "Merge failed for Issue #12" is displayed
