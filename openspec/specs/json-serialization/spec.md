# OpenSpec Capability: json-serialization

### Requirement: Server exposes a single JSON serialization entry point

Server SHALL expose `Mohist.Server.Infrastructure.JSON` as the sole source of `JsonSerializerOptions` and the sole entry point for JSON serialize/deserialize in server code. All `System.Text.Json` serialize/deserialize calls in `packages/server/src` SHALL go through `JSON.*` — either via facade helpers (e.g. `JSON.Serialize`, `JSON.Deserialize`) or by passing `JSON.Options` / `JSON.Indented` to `JsonSerializer.*`.

#### Scenario: Business code does not construct local serializer options

- **WHEN** the server source tree (`packages/server/src`, excluding the `JSON` facade itself) is inspected
- **THEN** there SHALL be no `new JsonSerializerOptions(` occurrences outside the `JSON` facade
- **AND** every `JsonSerializer.Serialize` / `JsonSerializer.Deserialize` call SHALL pass `JSON.Options`, `JSON.Indented`, or another options instance sourced from `JSON.*`

#### Scenario: Middle-layer shared options delegate to the unified facade

- **WHEN** a previously shared options field (e.g. `CloudEvent.JsonOptions`, `VariableBundle.JsonOptions`, `WorkflowYamlSerializer.JsonOptions`, `WorkflowVariableJson.Options`, `AgentSessionJson`) is used
- **THEN** it SHALL resolve to `JSON.Options` (or a documented narrow variant) rather than an independently constructed `JsonSerializerOptions`

### Requirement: Unified options preserve non-ASCII characters via a safe encoder

`JSON.Options` SHALL configure `Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)` so non-ASCII characters (e.g. Chinese) are emitted verbatim rather than as `\uXXXX` escape sequences. The encoder SHALL continue to escape characters that are HTML-dangerous, preserving XSS-safe output.

#### Scenario: Non-ASCII text is serialized verbatim

- **WHEN** a string containing non-ASCII characters (e.g. `"中文"`) is serialized through `JSON.*`
- **THEN** the output SHALL contain the original characters verbatim
- **AND** the output SHALL NOT contain `\uXXXX` style escape sequences for those characters

#### Scenario: HTML-dangerous characters remain escaped

- **WHEN** a string containing HTML-significant characters (e.g. `<`, `>`, `&`) is serialized through `JSON.*`
- **THEN** those characters SHALL remain escaped in the output
- **AND** the output SHALL remain safe to embed in HTML contexts

### Requirement: HTTP API layer reuses the unified options for all responses

The HTTP API layer SHALL register the unified `JSON.Options` as the global `Microsoft.AspNetCore.Http.Json.JsonOptions` so that every outbound response path — including `Results.Ok` / `Results.Json`, shared `ApiResults.*` helpers, and the unhandled-exception middleware — serializes with the non-ASCII-preserving encoder rather than a per-call options instance.

#### Scenario: API wiring uses a single global options registration

- **WHEN** the server configures `Microsoft.AspNetCore.Http.Json.JsonOptions`
- **THEN** the registered serializer options SHALL be `JSON.Options`
- **AND** individual response helpers (`Results.Ok` / `Results.Json`, `ApiResults.*`, exception middleware) SHALL NOT override the encoder with a locally constructed options

### Requirement: SignalR hubs reuse the unified options

Both SignalR hubs (`/hubs/runner` and `/hubs/events`) SHALL register the unified `JSON.Options` as the `Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions` payload serializer options so that pushed event payloads preserve non-ASCII characters.

#### Scenario: Hub payloads preserve non-ASCII characters

- **WHEN** a hub pushes an event whose payload contains non-ASCII characters (e.g. a runner event with Chinese content)
- **THEN** the delivered client payload SHALL contain the original characters verbatim
- **AND** the payload SHALL NOT contain `\uXXXX` escape sequences for those characters

### Requirement: Custom converters are centrally owned and behavior-preserving

Custom JSON converters (e.g. `FailureReason`, `ApprovalFeedbackStatus`, `AgentSessionStore`) SHALL be registered on `JSON.Options`, or retained only as documented narrow variants that delegate to it. The migration SHALL NOT change the serialized representation of enums or session state compared to the pre-change behavior.

#### Scenario: Enum conversion behavior is preserved

- **WHEN** a type previously serialized via a custom enum converter (e.g. `FailureReason`, `ApprovalFeedbackStatus`) is serialized after the change
- **THEN** the JSON representation SHALL match the pre-change representation (e.g. enum serialized as string)
- **AND** round-trip deserialization SHALL reproduce the original value

#### Scenario: Session state conversion behavior is preserved

- **WHEN** `AgentSessionStore` or equivalent session-state types are serialized and deserialized after the change
- **THEN** the JSON representation SHALL match the pre-change representation
- **AND** deserialization SHALL reconstruct the original session state

### Requirement: Deserialization stays backward-compatible with persisted JSON

Changing the encoder SHALL affect only serialization output. JSON previously persisted to SQLite or on-disk files (config, artifacts, session stores) under the prior encoding SHALL deserialize correctly through `JSON.*` without a data migration or schema change.

#### Scenario: Previously persisted JSON reads back unchanged

- **WHEN** JSON that was persisted before this change — whether it contained `\uXXXX` escapes or verbatim characters — is deserialized through `JSON.*`
- **THEN** the resulting object values SHALL equal the originally persisted values
- **AND** no data migration step SHALL be required
