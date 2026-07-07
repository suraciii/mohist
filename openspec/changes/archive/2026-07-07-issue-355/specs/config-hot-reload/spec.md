### Requirement: Config source reloads on file change via native AddJsonFile

`AddMohistConfigFile` SHALL register `~/.mohist/config.jsonc` (or the provided path) through the native `AddJsonFile(configPath, optional: true, reloadOnChange: true)` pipeline. The `reloadOnChange` parameter MUST be wired into the real `AddJsonFile` call (no dead parameter), and the resulting `IConfiguration` MUST emit a change token when the file is edited on disk so downstream options bindings can observe the new values without a server restart.

#### Scenario: editing config.jsonc at runtime triggers a configuration reload

- **WHEN** the server is running and `~/.mohist/config.jsonc` is edited on disk (e.g. `Mohist:WorkspaceCleanup:StorageBudgetBytes` is changed from one value to another)
- **THEN** the `IConfiguration` change token fires and the bound options reflect the new value, with no server restart or `mo update server` required.

#### Scenario: reloadOnChange parameter is honored, not a dead parameter

- **WHEN** `AddMohistConfigFile` is invoked with `reloadOnChange: true`
- **THEN** a real file watcher (`PhysicalFileProvider`) is attached through `AddJsonFile`, and the parameter actually controls reload behavior (as opposed to being silently ignored by an `AddJsonStream` one-shot path).

#### Scenario: missing config file is tolerated as optional

- **WHEN** `~/.mohist/config.jsonc` does not exist at startup
- **THEN** the server starts normally without throwing, mirroring the prior `optional` semantics — the absence of the file is not a fatal error.

### Requirement: JSONC is parsed natively without custom comment stripping

The server SHALL parse `config.jsonc` (including `//` line comments, `/* */` block comments, and trailing commas) using the standard .NET JSON pipeline's built-in comment-skipping (`ReadCommentHandling = Skip` / `AllowTrailingCommas`). The hand-rolled `StripJsoncComments` preprocessor SHALL be removed entirely; no custom comment-stripping code SHALL remain on the config-load path. Existing `config.jsonc` files that contain comments MUST continue to parse correctly.

#### Scenario: config.jsonc with line comments loads correctly

- **WHEN** `config.jsonc` contains `//` line comments interspersed among configuration keys
- **THEN** the configuration loads successfully and every configured key is bound to its value, with the comments ignored by the native JSON parser.

#### Scenario: config.jsonc with block comments and trailing commas loads correctly

- **WHEN** `config.jsonc` contains `/* */` block comments and/or trailing commas
- **THEN** the configuration loads successfully via the native `AddJsonFile` path with no preprocessing, and all configured values are available through `IConfiguration`.

#### Scenario: StripJsoncComments is not present on the config-load path

- **WHEN** the configuration is loaded at server startup
- **THEN** no `StripJsoncComments` (or equivalent hand-rolled comment-stripping) call is invoked — comments are handled exclusively by `System.Text.Json` / `AddJsonFile`.

### Requirement: ConfigService reads and writes JSONC natively

The two `ConfigService` call sites that previously relied on `StripJsoncComments` (`ReadConfigFile` and `WriteConfigFileAsync`) SHALL parse `config.jsonc` directly with `JsonNode.Parse` / `JsonDocument.Parse` using `JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }` (or the equivalent `System.Text.Json` comment-skipping option). The read and round-trip (read → modify field → write back) behavior SHALL be preserved — an existing `config.jsonc` with comments MUST still be readable, and a field update SHALL still be writable. No hand-rolled comment stripper SHALL be used in either method, and the two code paths SHALL NOT be left in an old-and-new coexistence state.

#### Scenario: ReadConfigFile parses a config.jsonc that contains comments

- **WHEN** `ConfigService.ReadConfigFile` reads a `config.jsonc` containing `//` or `/* */` comments
- **THEN** the file parses into the flattened key-value dictionary successfully (comments skipped natively by `System.Text.Json`), and every configured key is available just as before the change.

#### Scenario: WriteConfigFileAsync round-trips a config.jsonc that contains comments

- **WHEN** `ConfigService.WriteConfigFileAsync` reads an existing `config.jsonc` (which may contain comments), modifies a single field, and writes it back
- **THEN** the read step parses the file with native comment-skipping (no `StripJsoncComments`), the targeted field is updated, and the file is written back. (Preservation of the original comments on write-back is out of scope for this change.)

#### Scenario: malformed config.jsonc in ConfigService degrades to empty/default

- **WHEN** `ConfigService.ReadConfigFile` or `WriteConfigFileAsync` encounters a `config.jsonc` that fails to parse (e.g. genuinely malformed JSON, not just comments/trailing commas)
- **THEN** the methods continue to degrade gracefully (`ReadConfigFile` returns an empty dictionary, `WriteConfigFileAsync` falls back to a fresh `JsonObject`) — no exception escapes, matching today's fault-tolerance.

### Requirement: Reload or watcher failure must not block startup

A malformed `config.jsonc`, a file-watcher failure, or a reload error SHALL NOT prevent the server from starting or crash a running server. The fault-tolerance semantics of the prior `AddJsonStream` + try/catch path MUST be preserved: the config source is best-effort, and any failure on the load/reload path degrades to the last-known-good (or empty) configuration rather than aborting startup. This accommodates filesystems (network mounts, container bind mounts) where native file-watch notifications may behave differently or be unavailable.

#### Scenario: malformed config.jsonc at startup does not abort server start

- **WHEN** `config.jsonc` exists at startup but is not parseable as JSON/JSONC
- **THEN** the server still starts successfully, logging the parse problem, and configuration falls back to defaults / environment variables rather than throwing during host build.

#### Scenario: a reload error at runtime does not crash the server

- **WHEN** the file watcher detects a change but the new file content fails to parse (e.g. an editor wrote a half-saved file)
- **THEN** the server continues running using the previously-bound configuration, rather than crashing or entering an unrecoverable state.
