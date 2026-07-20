### Requirement: Every executable Action has one declarative definition

The Runner SHALL define every executable Action as one manifest paired with one execution function. The manifest MUST declare a canonical lowercase `<namespace>/<action>` name, its top-level inputs, successful output fields, and business error codes. Each input MUST declare exactly one JSON type from `string`, `number`, `boolean`, `object`, or `array`. An input SHALL be optional, required, or defaulted, MUST NOT be both required and defaulted, and any declared default MUST match the input's declared type. The engine-reserved `working-directory` input MUST NOT be declared by an Action manifest.

#### Scenario: Define a complete Action
- **WHEN** an Action author provides a valid manifest and its execution function
- **THEN** the Runner SHALL derive the Action's registration and contract metadata from that definition
- **AND** no separate name-to-handler registration SHALL be required

#### Scenario: Reject an invalid Action definition
- **WHEN** an Action definition has a non-canonical name, an unsupported input type, a default of the wrong type, a required input with a default, or an input named `working-directory`
- **THEN** registry construction MUST reject the definition before the Runner accepts work

### Requirement: The manifest collection is the Action registry authority

The Runner SHALL construct Action resolution and catalog data from the same manifest collection. Action selection SHALL compare `uses` with canonical manifest names case-insensitively. The collection MUST contain at most one executable manifest for each case-insensitive name, and the Runner MUST NOT expose an executable Action that is absent from the collection.

#### Scenario: Resolve a declared Action
- **WHEN** a dispatch selects a manifest name using different letter casing
- **THEN** the Runner SHALL resolve and execute the Action declared by that manifest

#### Scenario: Reject duplicate manifest names
- **WHEN** two executable manifests declare names that are equal under case-insensitive comparison
- **THEN** registry construction MUST fail rather than silently replacing either Action

#### Scenario: An unknown Action is not executable
- **WHEN** a dispatch selects a name that is absent from both executable manifests and tombstones
- **THEN** the Runner SHALL fail the work as an unknown Action
- **AND** no Action execution function SHALL run

### Requirement: Removed Actions are represented by catalog tombstones

The Action collection SHALL support tombstones containing a canonical Action name and actionable removal guidance. A tombstone name MUST NOT also identify an executable manifest. Task and check dispatch SHALL resolve tombstones case-insensitively and MUST distinguish a removed Action from an unknown Action.

#### Scenario: Dispatch a removed Action
- **WHEN** a task or check selects an Action name represented by a tombstone
- **THEN** the Runner SHALL fail that task or check without invoking an execution function
- **AND** the failure message SHALL identify the selected Action as removed
- **AND** the failure message SHALL include the tombstone's recovery or replacement guidance

#### Scenario: Removed and unknown Actions remain distinguishable
- **WHEN** one dispatch selects a tombstoned Action and another selects a name absent from the catalog
- **THEN** the two dispatches SHALL produce distinguishable removed-Action and unknown-Action failures

### Requirement: Runner registration publishes the serializable Action catalog

The Runner's registration state SHALL include a serializable Action catalog derived from its manifest collection. The catalog MUST include each executable Action's name, input declarations, output declarations, and business error declarations, plus every tombstone's name and guidance; it MUST exclude execution functions and other implementation-only values. The Server SHALL accept and retain the catalog carried by the Runner's latest registration state without using it to reject Profile saves in this change.

#### Scenario: Register a Runner with its Action catalog
- **WHEN** a Runner registers or repairs its registration state
- **THEN** it SHALL send the catalog derived from the same collection used for local Action resolution
- **AND** the Server SHALL retain that catalog as part of the Runner's current information

#### Scenario: Catalog serialization excludes executable code
- **WHEN** the Runner serializes its Action catalog for registration
- **THEN** the payload SHALL contain only manifest contract data and tombstones
- **AND** it MUST NOT contain an Action execution function

### Requirement: Built-in Actions are fully manifest-backed

Every built-in Action shipped by the Runner SHALL be present in the manifest collection, and no built-in Action SHALL use a separate registration path. Migrating built-ins to manifests MUST preserve their behavior for inputs valid under their declared contracts, including the existing built-in workflow profiles and Action-specific execution behavior.

#### Scenario: Built-in profiles resolve only manifest-backed Actions
- **WHEN** each shipped built-in workflow profile is traversed across stage tasks, checks, approval feedback tasks, and recovery tasks
- **THEN** every referenced executable Action SHALL resolve to a built-in manifest

#### Scenario: Built-in workflow regression remains valid
- **WHEN** the shipped built-in profiles run with valid inputs after manifest migration
- **THEN** their existing end-to-end workflow behavior SHALL remain unchanged except for the newly specified input rejection behavior

### Requirement: Platform and Action error codes have separate ownership

The platform SHALL own the reserved error codes `invalid-input`, `unexpected-error`, and `timeout`; those codes SHALL remain available for their platform-defined failure conditions without appearing in an Action's business error catalog. An Action manifest MUST NOT declare a reserved platform code as a business error, and an Action-produced business failure MUST use a code declared by that Action's manifest. Structured task errors SHALL preserve either kind of code so recovery conditions can match platform codes and Action-owned codes without matching human-readable messages.

#### Scenario: Recover from an Action-owned error
- **WHEN** an Action returns a business error declared by its manifest and a task recovery condition matches that error code
- **THEN** the Runner SHALL expose the declared code in the task's structured error context
- **AND** the matching recovery handler SHALL be eligible to run

#### Scenario: Recover from a platform error
- **WHEN** a task produces the platform-defined `invalid-input`, `unexpected-error`, or `timeout` failure and a recovery condition matches that code
- **THEN** the Runner SHALL expose the platform code in the same structured error context
- **AND** the matching recovery handler SHALL be eligible to run

#### Scenario: Reject a reserved business error declaration
- **WHEN** an Action manifest declares `invalid-input`, `unexpected-error`, or `timeout` as an Action-owned business error
- **THEN** registry construction MUST reject the manifest
