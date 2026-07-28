### Requirement: Wire-format status mapping is typed per enum, not string

The mapping from a run/stage/task/check status enum to its wire-format string MUST accept the typed enum, not a `string`. There SHALL be one mapping entry per consuming enum (`WorkflowRunStatus`, `StageRunStatus`, `TaskRunStatus`, `StageCheckStatus`), each a switch expression over that enum. The single `FrontendStatus(string raw)` entry point that today receives every enum's `ToString()` MUST be removed from production code.

#### Scenario: Each enum has its own typed mapping entry

- **WHEN** a status view is built for a run, stage, task, or check
- **THEN** the wire value MUST be produced by calling the mapping entry whose parameter is that specific enum type
- **AND** the call site MUST NOT pass `someStatus.ToString()` into a string-typed mapper

#### Scenario: No string-typed status mapper remains in production

- **WHEN** the server production source is searched for a status mapper whose parameter is `string`
- **THEN** no such production mapper SHALL exist

### Requirement: Mapping is exhaustive by construction with no string fallback

Every typed mapping entry MUST be a switch expression that covers all values of its enum with no `_` (discard) fallback and no `ToLowerInvariant()` fallback. Adding a new value to any of the four enums without adding its mapping MUST be a compile error, so that a new multi-word enum value can never silently produce a wrong wire token like `inprogress`.

#### Scenario: New enum value without a mapping fails to compile

- **WHEN** a value is added to `WorkflowRunStatus`, `StageRunStatus`, `TaskRunStatus`, or `StageCheckStatus` and no corresponding mapping arm is added
- **THEN** the build MUST fail with a non-exhaustive switch error

#### Scenario: No ToLowerInvariant fallback exists

- **WHEN** the typed mapping entries are inspected
- **THEN** none SHALL contain a `ToLowerInvariant()` call or a discard arm that derives the wire value from the enum name at runtime

### Requirement: Every emitted wire value is kebab-case and byte-identical to today

Every enum value MUST map to a lowercase kebab-case token. The set of wire values emitted after this change MUST be byte-for-byte identical to the values emitted before this change, including the special-cased `awaiting-approval`. No existing wire token SHALL be renamed, reordered in meaning, or dropped.

#### Scenario: AwaitingApproval maps to awaiting-approval across enums

- **WHEN** `WorkflowRunStatus.AwaitingApproval` or `StageRunStatus.AwaitingApproval` is mapped
- **THEN** the wire value MUST be exactly `awaiting-approval`

#### Scenario: Single-word values map to their lowercase form

- **WHEN** any single-word enum value (e.g. `Created`, `Running`, `Passed`, `Failed`) is mapped
- **THEN** the wire value MUST be exactly the lowercase single word (e.g. `created`, `running`, `passed`, `failed`)

#### Scenario: Exhaustive per-enum coverage

- **WHEN** `Enum.GetValues` is enumerated for each of the four enums
- **THEN** every value MUST have a mapping arm
- **AND** every mapped result MUST match the kebab-case form of that value

### Requirement: Mapper symbol named for the wire-format contract

The mapping symbol MUST be named to express that it defines a wire-format contract (e.g. `WireStatus`), not a "frontend" concept. The name `FrontendStatus` SHALL NOT remain in production code, because the CLI also consumes this table and it describes a line format, not a frontend.

#### Scenario: FrontendStatus name is gone

- **WHEN** the server production source is searched for `FrontendStatus`
- **THEN** zero matches MUST appear
- **AND** all call sites MUST reference the renamed wire-format symbol

### Requirement: Web status unions mirror their authoritative server enums

Each of the four web status unions (`WorkflowRunStatus`, `WorkflowStageRunStatus`, `StageStateStatus`, `WorkflowRecoverySummary`) MUST include every wire value its authoritative server enum emits, and each union type MUST carry a comment naming the server enum that is the source of truth. The web unions MAY legitimately model additional client-only states, but they MUST NOT omit a wire value that the server can emit.

#### Scenario: Each web union names its authoritative server enum

- **WHEN** each of the four web status union types is inspected
- **THEN** a comment at the type declaration MUST identify the server enum whose wire values it mirrors

#### Scenario: No server-emitted wire value is missing from the web union

- **WHEN** a typed mapping entry emits a wire value for any enum
- **THEN** the corresponding web union MUST include that value as a permitted member
