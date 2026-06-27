## ADDED Requirements

### Requirement: Create-issue success toast shows the new issue number

The Web UI create-issue flow SHALL confirm a successful creation with a success toast that displays the newly created issue's `number`. The create-issue mutation SHALL read `number` from the create API response's `Issue.number` field when building the toast message. The toast SHALL render a concrete number (e.g. `Issue #223 created`) and SHALL NOT render `undefined` or any other placeholder in place of the number. A failed create SHALL surface an error toast that does not reference an undefined number.

#### Scenario: Successful create shows correct issue number in toast

- **WHEN** a user submits the create-issue form and the create API returns an `Issue` with `number: 223`
- **THEN** the Web UI SHALL show a success toast containing the literal `Issue #223 created`
- **AND** the toast SHALL NOT display `undefined` in place of the number

#### Scenario: Create toast reads number from the create response

- **WHEN** the create-issue mutation's `onSuccess` handler runs with the API response
- **THEN** the toast message SHALL be built from the `Issue.number` field of the create response
- **AND** the handler SHALL NOT read the number from an undefined or mismatched response field

#### Scenario: Failed create shows error toast without an undefined number

- **WHEN** a create-issue request fails
- **THEN** the Web UI SHALL surface an error toast describing the failure
- **AND** the error toast SHALL NOT reference any issue number (and in particular SHALL NOT render `undefined`)
