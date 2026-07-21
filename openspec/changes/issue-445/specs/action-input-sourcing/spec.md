### Requirement: Built-in Action inputs have one source

Every built-in Action SHALL derive all Action-owned input exclusively from the rendered and manifest-validated `with` payload. A built-in Action MUST NOT inspect Run Variables or effective Variables to obtain an omitted input, choose an alternative input value, or supplement the validated payload. Engine-owned execution context, including the resolved working directory, SHALL remain available as host context and MUST NOT be treated as an Action input fallback.

#### Scenario: Omitted input is not recovered from Variables
- **WHEN** a built-in Action's rendered `with` payload omits an Action input and the same semantic value exists in Run Variables
- **THEN** the Action SHALL behave as though that input was omitted
- **AND** the Action MUST NOT read or use the Variable value

#### Scenario: Explicit Variable binding becomes ordinary Action input
- **WHEN** a workflow binds a Variable through `${{ vars.* }}` in `with` and template expansion produces a valid declared input
- **THEN** the Action SHALL consume the rendered value from `with`
- **AND** subsequent Variable changes MUST NOT alter that Action invocation's input

#### Scenario: Host working directory remains engine-owned
- **WHEN** the engine invokes a built-in Action with a resolved working directory
- **THEN** the Action SHALL execute in that host-provided directory
- **AND** the Action MUST NOT select a different directory from Run Variables

### Requirement: Former implicit inputs are declared and enforced

Each built-in Action input that affects workspace preparation, repository selection, source or target branches, Git remotes, pull request selection, or other execution behavior SHALL be declared in that Action's manifest with its required status, accepted type, and default where applicable. If a required value is absent from rendered `with`, dispatch SHALL fail with platform error code `invalid-input` before the Action performs commands, network calls, or other side effects; it MUST NOT fall back to Variables or conventional values not declared as manifest defaults.

#### Scenario: Missing required delivery input fails before execution
- **WHEN** rendered `with` omits a delivery input marked required by the selected built-in Action manifest
- **THEN** dispatch SHALL fail with error code `invalid-input`
- **AND** the error message SHALL identify the missing input
- **AND** the Action MUST NOT perform a Git or GitHub operation

#### Scenario: Manifest default remains explicit contract behavior
- **WHEN** rendered `with` omits an optional input that has a manifest default
- **THEN** manifest validation SHALL place that declared default in the validated input
- **AND** the Action SHALL use the validated default without consulting Variables

### Requirement: Declared delivery inputs are authoritative for the invocation

Delivery Actions SHALL use their validated repository, branch, remote, and pull request inputs as the complete delivery request. They MUST NOT compare those values with issue-backed repository or workspace data from Run Variables, reject a valid declared value because such hidden context differs, or replace a declared value with issue-derived context. Delivery authorization SHALL continue to be determined by the credentials used for the external operation; this change MUST NOT add a server-side dispatch policy check.

#### Scenario: Declared delivery value is not cross-checked against hidden context
- **WHEN** a delivery Action receives a valid declared branch, remote, or repository input and Run Variables contain a different value for the same concept
- **THEN** the Action SHALL execute using the declared input
- **AND** it MUST NOT reject or rewrite the input because the Run Variable differs

#### Scenario: Credentials still govern external access
- **WHEN** a delivery Action receives complete valid inputs but its Git or GitHub credentials do not authorize the requested operation
- **THEN** the Action SHALL report the existing external-operation failure
- **AND** the system MUST NOT treat an issue-derived value comparison as an authorization boundary

### Requirement: Published Action contracts expose every supported input

The manifest-derived Action catalog and user documentation SHALL describe the same complete input surface used by each supported built-in Action. Documentation MUST NOT describe an implicit Variable fallback, and every documented Variable-dependent example SHALL bind that Variable explicitly through `with`.

#### Scenario: Catalog and documentation match runtime input behavior
- **WHEN** a user inspects a supported built-in Action's catalog entry and documentation
- **THEN** every Action-owned value that can affect runtime behavior SHALL appear as a declared input
- **AND** omitting an undocumented Variable value MUST NOT change the Action's behavior
