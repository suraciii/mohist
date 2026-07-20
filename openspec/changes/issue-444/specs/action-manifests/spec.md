### Requirement: Every executable Action has one declarative definition

The Runner SHALL define every executable Action as one manifest paired with one execution function. The manifest MUST declare a canonical lowercase `<namespace>/<action>` name, its top-level inputs, successful output fields, and business error codes. Each input MUST declare a non-empty finite set of accepted JSON kinds chosen from `string`, `number`, `boolean`, `object`, and `array`; the set MUST NOT contain duplicates. An input SHALL be optional, required, or defaulted, MUST NOT be both required and defaulted, and any declared static default MUST have one of the input's accepted kinds. `null` is not an accepted kind and MUST NOT be a default. The engine-reserved `working-directory` input MUST NOT be declared by an Action manifest.

#### Scenario: Define a complete Action
- **WHEN** an Action author provides a valid manifest and its execution function
- **THEN** the Runner SHALL derive the Action's registration and contract metadata from that definition
- **AND** no separate name-to-handler registration SHALL be required

#### Scenario: Reject an invalid Action definition
- **WHEN** an Action definition has a non-canonical name, an empty or duplicate kind set, an unsupported input kind, a default whose kind is not accepted, a required input with a default, a null default, or an input named `working-directory`
- **THEN** registry construction MUST reject the definition before the Runner accepts work

#### Scenario: Declare both supported OpenCode prompt forms
- **WHEN** `mohist/opencode` declares required input `prompt` with accepted kinds `string` and `object`
- **THEN** the manifest SHALL represent both kinds as one input contract
- **AND** its execution function SHALL infer `prompt` as a string-or-object value

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

The Runner's registration state SHALL include a serializable Action catalog derived from its manifest collection. The catalog root MUST contain `actions` and `tombstones` arrays. Each Action entry MUST contain `name`, an `inputs` array of `{ name, types, required, default?, description? }`, an `outputs` array of `{ name, description? }`, and an `errors` array of `{ code, description }`. An Action description SHALL be included only when the manifest declares one. Every input's `types` MUST use the canonical order `string`, `number`, `boolean`, `object`, `array`, including a one-element array for a single-kind input. Each tombstone MUST contain `name` and `guidance`. Actions, inputs, outputs, errors, and tombstones MUST be ordered lexicographically by name or code. The catalog MUST exclude execution functions and other implementation-only values. The Server SHALL accept and retain the catalog carried by the Runner's latest registration state without using it to reject Profile saves in this change.

#### Scenario: Register a Runner with its Action catalog
- **WHEN** a Runner registers or repairs its registration state
- **THEN** it SHALL send the catalog derived from the same collection used for local Action resolution
- **AND** the Server SHALL retain that catalog as part of the Runner's current information

#### Scenario: Catalog serialization excludes executable code
- **WHEN** the Runner serializes its Action catalog for registration
- **THEN** the payload SHALL contain only manifest contract data and tombstones
- **AND** it MUST NOT contain an Action execution function

#### Scenario: Catalog serializes a finite input union canonically
- **WHEN** the Runner serializes `mohist/opencode.prompt`, which accepts string and object values
- **THEN** its catalog input SHALL contain `"types": ["string", "object"]`
- **AND** a later consumer SHALL need no Action-specific rule to recognize either accepted kind

### Requirement: Built-in Actions are fully manifest-backed

Every built-in Action shipped by the Runner SHALL be present in the manifest collection, and no built-in Action SHALL use a separate registration path. Except for the explicit new rejection of unknown top-level fields, exact-kind enforcement, and rejection of explicit `null`, each built-in manifest and implementation MUST preserve the pre-migration contract for every known input key: aliases, conditional requirements, static defaults, dynamic/context fallbacks, nested semantics, public outputs, and business error codes. Ignored keys removed from shipped profiles are not Action inputs. The migration baseline MUST be recorded as one auditable inventory and verified independently of the shipped profile subset.

#### Scenario: Built-in profiles resolve only manifest-backed Actions
- **WHEN** each shipped built-in workflow profile is traversed across stage tasks, checks, approval feedback tasks, and recovery tasks
- **THEN** every referenced executable Action SHALL resolve to a built-in manifest

#### Scenario: Built-in workflow regression remains valid
- **WHEN** the shipped built-in profiles run with valid inputs after manifest migration
- **THEN** their existing end-to-end workflow behavior SHALL remain unchanged except for the newly specified input rejection behavior

#### Scenario: Preserve a known alias outside shipped profiles
- **WHEN** a custom profile invokes `core/marker` with legacy alias `contains`
- **THEN** the manifest SHALL recognize `contains` as a string input
- **AND** the Action SHALL preserve its existing precedence and marker behavior

#### Scenario: Preserve an implicit context fallback
- **WHEN** a built-in Action's recorded migration baseline allows an input to be omitted because Variables or execution context supplies it
- **THEN** that manifest input SHALL remain optional
- **AND** the Action SHALL preserve the recorded fallback behavior

#### Scenario: Declare every emitted business error
- **WHEN** a built-in Action can return a business error through a static, classified, or fallback branch
- **THEN** that error code MUST appear in the Action's manifest inventory
- **AND** automated coverage SHALL prove that emitted business codes are a subset of the declared codes

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

#### Scenario: Normalize an undeclared business error
- **WHEN** an Action execution function returns an error code that is neither reserved by the platform nor declared by its manifest
- **THEN** the Runner SHALL replace it with `unexpected-error`
- **AND** a task SHALL expose that structured error to normal recovery matching

#### Scenario: Normalize an Action exception
- **WHEN** an Action execution function throws before returning a result
- **THEN** the Runner SHALL produce `unexpected-error` with an actionable message
- **AND** a task SHALL expose that structured error to normal recovery matching

#### Scenario: Reject a malformed task Action result
- **WHEN** a task Action returns a non-object result, both or neither of `output` and `error`, a non-string error code or message, or a success output that is not a JSON object or null
- **THEN** the Runner SHALL produce `unexpected-error`
- **AND** the task SHALL expose that structured error to normal recovery matching

#### Scenario: Preserve check failure aggregation for contract errors
- **WHEN** an individual check Action returns an undeclared error or throws
- **THEN** that check row SHALL contain `unexpected-error`
- **AND** the aggregate check verdict SHALL remain `check-failed`

#### Scenario: Reject a malformed check Action result
- **WHEN** an individual check Action returns a non-object result, both or neither of `output` and `error`, or a non-string error code or message
- **THEN** that check row SHALL contain `unexpected-error`
- **AND** the aggregate check verdict SHALL remain `check-failed`

#### Scenario: Preserve invalid-output check handling
- **WHEN** an individual check Action returns a success output that is not a JSON object or null
- **THEN** the check dispatch SHALL retain the existing aggregate `unexpected-error` invalid-output failure
