### Requirement: Unified recoverable error surface for both failure paths

The Files/Diff evidence page (`/issues/<number>/files`) MUST render a single recoverable error surface when the evidence cannot be loaded, regardless of whether the failure originates from a transport/query error (HTTP failure, network error) or a server-reported unavailability reason (`runner_unavailable`, `workspace_removed`, `branch_missing`, `git_error`, `not_started`). The two failure paths SHALL NOT produce distinct dead-end states — both MUST converge on the same recoverable surface that preserves issue context and offers recovery actions.

#### Scenario: Transport/query error renders the recoverable surface

- **WHEN** the issue, diff, or commits query fails with a transport or HTTP error
- **THEN** the page MUST render the recoverable error surface instead of a bare error banner that strips issue context
- **AND** the surface MUST NOT strand the user with only a navigation link and no retry

#### Scenario: Server-reported runner unavailability renders the recoverable surface

- **WHEN** the diff or commits response reports `available: false` with reason `runner_unavailable`
- **THEN** the page MUST render the recoverable error surface with the same recovery actions as the transport-error path
- **AND** the surface MUST NOT render a bare availability banner with no next step

#### Scenario: Server-reported workspace removal renders the recoverable surface

- **WHEN** the diff or commits response reports `available: false` with reason `workspace_removed`
- **THEN** the page MUST render the recoverable error surface with the same recovery actions as the transport-error path

#### Scenario: Server-reported branch missing renders the recoverable surface

- **WHEN** the diff or commits response reports `available: false` with reason `branch_missing`
- **THEN** the page MUST render the recoverable error surface with the same recovery actions as the transport-error path

#### Scenario: Server-reported git error renders the recoverable surface

- **WHEN** the diff or commits response reports `available: false` with reason `git_error`
- **THEN** the page MUST render the recoverable error surface with the same recovery actions as the transport-error path
- **AND** the surface MUST NOT fall back to a generic message that drops the git-error cause

#### Scenario: Not-started state renders the recoverable surface

- **WHEN** the diff or commits response reports `available: false` with reason `not_started`
- **THEN** the page MUST render the recoverable error surface so the user is not stranded, with retry, return-to-issue, and related-session-link actions available alongside the explanation

### Requirement: Issue context preserved on the error surface

The recoverable error surface MUST keep the issue context visible alongside the failure explanation. The visible context SHALL include the issue number, the issue title, and the issue health badge. A load failure MUST NOT replace the issue context with a banner-only view that drops the title or health badge.

#### Scenario: Issue context remains visible when evidence fails to load

- **WHEN** the diff evidence cannot be loaded for a valid issue that has already been fetched
- **THEN** the error surface MUST display the issue number, the issue title, and the health badge
- **AND** the failure explanation MUST appear alongside that context, not in its place

#### Scenario: Issue context is shown when only the issue loads but diff is unavailable

- **WHEN** the issue query succeeds but the diff query reports unavailability or fails
- **THEN** the error surface MUST still display the issue number, title, and health badge from the loaded issue

### Requirement: Failure explained in product language

The recoverable error surface MUST explain the failure in product language that tells the user what could not be loaded and, where known, why. The surface SHALL NOT surface raw error identifiers, HTTP status codes, or bare connection identifiers as the user's primary guidance. When the server reports a specific unavailability reason, the surface MUST translate it into a human-readable explanation.

#### Scenario: Transport error is explained in product language

- **WHEN** the diff query fails with an HTTP or network error
- **THEN** the surface MUST display a product-language message stating that the file changes could not be loaded
- **AND** the surface MUST NOT display the raw HTTP status code or raw error identifier as the primary guidance

#### Scenario: Runner unavailability is explained in product language

- **WHEN** the diff response reports `runner_unavailable`
- **THEN** the surface MUST display a product-language message indicating the runner may be disconnected, rather than surfacing the raw `runner_unavailable` identifier as the user's guidance

#### Scenario: Not-started state is explained in product language

- **WHEN** the diff response reports `not_started`
- **THEN** the surface MUST display a product-language message indicating there are no changes yet, rather than surfacing the raw `not_started` reason

### Requirement: Retry action re-fetches evidence

The recoverable error surface MUST provide a retry action that re-fetches the failed evidence sources (issue, diff, and commits). Triggering retry MUST cause the page to re-request the evidence and re-evaluate the resulting state — rendering the evidence if the load now succeeds, or re-rendering the recoverable surface if it still fails.

#### Scenario: Retry re-fetches a failed diff load

- **WHEN** the diff query has failed and the user activates the retry action
- **THEN** the page MUST re-fetch the issue, diff, and commits sources
- **AND** if the diff load now succeeds, the page MUST render the evidence view

#### Scenario: Retry re-fetches after server-reported unavailability

- **WHEN** the diff response reported `runner_unavailable` and the user activates the retry action
- **THEN** the page MUST re-fetch the evidence sources
- **AND** if the runner is now connected and the diff is available, the page MUST render the evidence view

#### Scenario: Retry leaves the user on the recoverable surface when the failure persists

- **WHEN** the user activates retry and the evidence still cannot be loaded
- **THEN** the page MUST re-render the recoverable error surface with the same recovery actions still available

### Requirement: Return-to-issue navigation from the error surface

The recoverable error surface MUST provide a navigation action that returns the user to the issue detail page (`/issues/<number>`). This action MUST be available on every failure path covered by the recoverable surface.

#### Scenario: User returns to the issue detail page from a transport error

- **WHEN** the diff query has failed and the user activates the return-to-issue action
- **THEN** the page MUST navigate to the issue detail route for the current issue number

#### Scenario: User returns to the issue detail page from server-reported unavailability

- **WHEN** the diff response reported `workspace_removed` and the user activates the return-to-issue action
- **THEN** the page MUST navigate to the issue detail route for the current issue number

### Requirement: Related-session link when a session is known

The recoverable error surface MUST offer a link to open the issue's related workflow-run session when one is known. The session link is net-new for the page — the page currently has no session awareness. When no workflow-run session is known for the issue, the session link MUST be absent.

#### Scenario: Session link is offered when a workflow-run session exists

- **WHEN** the evidence cannot be loaded and the issue has a known workflow-run session
- **THEN** the error surface MUST display a link that opens the related session

#### Scenario: Session link is absent when no workflow-run session exists

- **WHEN** the evidence cannot be loaded and the issue has no known workflow-run session (no `workflowRunId`, or no sessions resolved for the run)
- **THEN** the error surface MUST NOT display a session link
- **AND** the retry and return-to-issue actions MUST still be available
