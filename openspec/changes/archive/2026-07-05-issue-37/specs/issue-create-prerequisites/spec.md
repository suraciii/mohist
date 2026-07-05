### Requirement: Create-issue request accepts an optional prerequisites field

The create-issue request body (`POST /projects/{projectRef}/issues`) SHALL accept an optional `prerequisiteNumbers` field: an array of zero or more issue numbers identifying existing issues in the same project that the new issue depends on. The field SHALL be optional; when omitted or empty the create SHALL behave exactly as it does today (an issue with no prerequisites). The field SHALL be sent as a JSON array of integers named `prerequisiteNumbers` (camelCase), consistent with the existing `AddPrerequisiteRequest.prerequisiteNumber` wire name.

#### Scenario: Creating an issue with no prerequisites field

- **WHEN** a client posts a create-issue request without a `prerequisiteNumbers` field
- **THEN** the issue SHALL be created with an empty prerequisite set
- **AND** the response `prerequisiteNumbers` SHALL be an empty array

#### Scenario: Creating an issue with an explicit empty prerequisites array

- **WHEN** a client posts `prerequisiteNumbers: []`
- **THEN** the issue SHALL be created with an empty prerequisite set, identical to omitting the field

#### Scenario: Creating an issue with one or more prerequisites

- **WHEN** a client posts `prerequisiteNumbers: [5, 7]` and issues #5 and #7 exist in the selected project
- **THEN** the created issue SHALL record both 5 and 7 as prerequisites
- **AND** the response `prerequisiteNumbers` SHALL contain `[5, 7]`

### Requirement: Prerequisite existence is validated against the selected project

Every number in `prerequisiteNumbers` SHALL refer to an existing issue in the same project as the issue being created (the project resolved from the request's `projectRef` / `projectId` / repository context). A number that does not resolve to an issue in that project SHALL be rejected with a clear validation error. The validation SHALL reuse the same project-scoped issue-existence check that `IIssueGrain.AddPrerequisiteAsync` applies on the single-add path, so create-time and edit-time existence semantics cannot diverge.

#### Scenario: Nonexistent prerequisite is rejected

- **WHEN** a client posts `prerequisiteNumbers: [99999]` and no issue #99999 exists in the selected project
- **THEN** the request SHALL fail with a validation error that names the offending number
- **AND** no issue SHALL be created

#### Scenario: Prerequisite from a different project is rejected

- **WHEN** issue #3 exists in project A but the create request targets project B with `prerequisiteNumbers: [3]`
- **THEN** the request SHALL fail validation because #3 does not exist in project B
- **AND** no issue SHALL be created in project B

### Requirement: Self-reference is rejected

Because the new issue's number is allocated before prerequisites are applied, a self-reference cannot be expressed by the client at create time (the client does not know the number in advance). Nevertheless the create path SHALL reject any prerequisite number that equals the newly allocated issue number, reusing the same self-reference guard that `Issue.AddPrerequisite` and `IIssueGrain.AddPrerequisiteAsync` enforce, so the invariant cannot be bypassed through the create endpoint.

#### Scenario: A prerequisite colliding with the new issue's own number is rejected

- **WHEN** the counter allocates number N for the new issue and the request lists N in `prerequisiteNumbers`
- **THEN** the request SHALL fail with a self-reference/circular validation error
- **AND** no readable issue SHALL be created

### Requirement: Duplicate prerequisite numbers are de-duplicated idempotently

When `prerequisiteNumbers` contains the same number more than once, the create path SHALL store that prerequisite exactly once, mirroring the idempotent behavior of `Issue.AddPrerequisite` on the single-add path. Duplicate entries SHALL NOT cause a failure.

#### Scenario: Repeated numbers collapse to a single prerequisite

- **WHEN** a client posts `prerequisiteNumbers: [5, 5, 5]`
- **THEN** the created issue SHALL record prerequisite 5 exactly once
- **AND** the response `prerequisiteNumbers` SHALL be `[5]`

### Requirement: Validation failure leaves no partially configured issue

If any prerequisite in `prerequisiteNumbers` fails validation (nonexistent, cross-project, or self-reference), the create endpoint SHALL fail the entire request without leaving a persisted, readable issue behind. The validation SHALL occur such that, on failure, no issue is returned to the client and a subsequent read of the would-be issue number SHALL report it as not found. This preserves the all-or-nothing contract of the create endpoint: prerequisites are applied atomically with creation.

#### Scenario: One invalid prerequisite among several rejects the whole request

- **WHEN** a client posts `prerequisiteNumbers: [5, 99999]` where #5 exists and #99999 does not
- **THEN** the request SHALL fail with a validation error naming #99999
- **AND** no issue SHALL be created
- **AND** a subsequent `GET /issues/{N}` for the would-be number N SHALL return not found

#### Scenario: No leftover issue after a failed create

- **WHEN** a create with an invalid prerequisite is rejected
- **THEN** the project's issue list SHALL not contain a new issue from this request
- **AND** the issue counter's next allocation SHALL reflect that no successful creation consumed the rejected number's slot per the established counter semantics

### Requirement: The created-issue response carries populated prerequisite and start-readiness read models

On a successful create with prerequisites, the `201` response data SHALL be the full issue read model (the same shape returned by `GET /issues/{number}`), with `prerequisiteNumbers`, `prerequisites` (the per-prerequisite summaries: number, title, status, health, completed), `canStart`, and `blocker` already populated to reflect the just-applied prerequisites. The client SHALL NOT need a second round-trip to render prerequisite state for the newly created issue.

#### Scenario: Response reflects prerequisites and start gate immediately

- **WHEN** an issue is created with `prerequisiteNumbers: [5]` and #5 is still undelivered
- **THEN** the response `prerequisiteNumbers` SHALL contain 5
- **AND** the response `prerequisites` SHALL include a summary for #5 with its title/status/completed flag
- **AND** the response `canStart` SHALL be false
- **AND** the response `blocker` SHALL identify #5 as the issue being waited on

#### Scenario: Start gate is open when all prerequisites are already completed

- **WHEN** an issue is created with `prerequisiteNumbers: [5]` and #5 is already completed/delivered
- **THEN** the response `prerequisites` SHALL mark #5 as completed
- **AND** the response `canStart` SHALL be true
- **AND** the response `blocker` SHALL be null

### Requirement: The New Issue dialog exposes an optional prerequisites selector

The New Issue dialog (`CreateIssueDialog.tsx`) SHALL render an optional `Prerequisites` field that lets the user select zero or more existing issues before submitting. Selected prerequisites SHALL be shown as removable chips while the dialog is open, so the user can review and remove choices before creation. On submit the dialog SHALL send the selected numbers as `prerequisiteNumbers` on the create request. The field SHALL default to empty, and submitting with no prerequisites SHALL create an issue with no prerequisites (unchanged behavior).

#### Scenario: Selecting prerequisites before submit

- **WHEN** the user selects issues #5 and #7 in the New Issue dialog and submits
- **THEN** the create request body SHALL include `prerequisiteNumbers: [5, 7]`

#### Scenario: Selected prerequisites appear as removable chips

- **WHEN** the user has selected #5 and #7 in the dialog
- **THEN** both SHALL be rendered as chips
- **AND** removing the #5 chip SHALL drop it from the selection before submit
- **AND** the submitted request SHALL then contain only `[7]`

#### Scenario: Dialog resets prerequisite selection after create

- **WHEN** a create succeeds and the dialog closes
- **THEN** the prerequisite selection SHALL be cleared along with the other fields, so the next open starts with no prerequisites

#### Scenario: No prerequisite field content still creates successfully

- **WHEN** the user submits the dialog without selecting any prerequisites
- **THEN** the request SHALL omit (or send an empty) `prerequisiteNumbers`
- **AND** the created issue SHALL have no prerequisites

### Requirement: Existing single-add/remove prerequisite API contract is unchanged

This change SHALL NOT alter the existing single-prerequisite HTTP contract (`POST /issues/{number}/prerequisites` and `DELETE /issues/{number}/prerequisites/{prerequisiteNumber}`). Those endpoints continue to accept one prerequisite number at a time and return the updated issue read model. The backlog detail editor MAY continue to use this contract behind the new picker UI; only the create endpoint gains the new atomic `prerequisiteNumbers` field.

#### Scenario: Single-add endpoint still works after this change

- **WHEN** a client posts `{ prerequisiteNumber: 5 }` to `POST /issues/{number}/prerequisites`
- **THEN** the endpoint SHALL behave exactly as before this change and return the updated issue read model

#### Scenario: Remove endpoint still works after this change

- **WHEN** a client deletes `DELETE /issues/{number}/prerequisites/5`
- **THEN** prerequisite 5 SHALL be removed and the updated issue read model SHALL be returned, as before this change
