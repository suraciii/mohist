## Context

Server serializes JSON through many independent `JsonSerializerOptions` instances, none of which set a `JavaScriptEncoder`. `System.Text.Json`'s default encoder escapes every non-ASCII character to `\uXXXX`, so Chinese (and all other non-ASCII text) shows up garbled in API responses, SignalR payloads, persisted JSON, runner-event serializers, logs, and artifacts. The second root cause is configuration drift: ~18 local `static readonly JsonSerializerOptions` fields are scattered across Workflow / Sessions / Issue / Project / Events / Api, each constructed with subtly different settings, so non-ASCII behavior is unpredictable per code path.

A unified facade already exists — `Mohist.Server.Infrastructure.JSON` (`packages/server/src/Mohist.Server/Infrastructure/JSON.cs`) — but it (a) sets no encoder, (b) has no indented variant, and (c) is not actually used by the scattered call sites. This change completes the convergence: make `JSON.Options` the single source of truth with a non-ASCII-preserving encoder, wire it into the HTTP and SignalR global options, and replace the local options fields.

**Current wiring points (verified):**
- HTTP layer: no `Microsoft.AspNetCore.Http.Json.JsonOptions` is configured anywhere → all `Results.Ok/Json`, `ApiResults.*`, and `UseApiExceptionHandler` middleware use framework defaults (no encoder).
- SignalR: `MohistServiceRegistration.ConfigureMohistServices` calls `services.AddSignalR()` (MohistServiceRegistration.cs:139) with no `.AddJsonProtocol(...)`; hubs mapped at `MohistApiRegistration.MapMohistApi` (lines 37–38: `/hubs/runner`, `/hubs/events`).

**Custom converters (verified) — two kinds:**
- *Attribute-based* — `ApprovalFeedbackStatusJsonConverter` is applied via `[JsonConverter(...)]` on `ApprovalFeedback.cs:6`. It travels with the type and works under any options instance.
- *Options-scoped* — `UnknownFailureReasonJsonConverter` (private nested class, `WorkflowQuerier.cs:187`) is manually registered only on `WorkflowQuerier.RunJsonOptions`. If that options field is removed, this converter must move to `JSON.Options` or `FailureReason` round-trips change.
- `JsonStringEnumConverter` (enum-as-string convention) is present on `JSON.Options` and most local fields already.

**Constraints:** server-only (.NET, `packages/server`); keep `System.Text.Json`; do not change field-naming (camelCase), enum-as-string, API paths, response shape, or `ApiResponse<T>`; no third-party serializer; encoder is output-direction only so persisted JSON reads back without migration.

## Goals / Non-Goals

**Goals:**
- Non-ASCII characters (e.g. Chinese) appear verbatim in all server JSON output: API responses, SignalR hub payloads, persisted files, event serializers.
- `JSON.Options` is the single `JsonSerializerOptions` source; no `new JsonSerializerOptions(` outside the facade, and all `JsonSerializer.Serialize/Deserialize` calls pass `JSON.*`.
- HTTP and SignalR reuse `JSON.Options` via one global registration each — no per-route/per-call options.
- Custom converter behavior (enum-as-string, `FailureReason`, `ApprovalFeedbackStatus`) is preserved exactly.

**Non-Goals:**
- Changing JSON field naming, enum string format, API routes, response structure, or `ApiResponse<T>`.
- Touching serialization in `packages/web` or `packages/runner`.
- Rewriting internal `JsonElement` merge logic — only the serialization entry point is swapped.
- Introducing a third-party serializer.

## Decisions

### 1. One static facade, not a DI options model

**Decision:** Keep `JSON` as a static facade (`Infrastructure/JSON.cs`). Add `Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)` to `JSON.Options` and add an `Indented` read-only variant (clones `Options` with `WriteIndented = true`). Wire HTTP/SignalR by assigning `JSON.Options` into the two framework options objects.

**Rationale:** The encoder is a global constant, not a per-request/per-tenant value. The facade is already used by domain and service code that does not take DI constructors (grains, stores, serializers). A static singleton avoids threading `IOptions<JsonSerializerOptions>` through ~40 call sites for zero behavioral benefit.

**Alternatives considered:**
- *DI-injected `IOptions<JsonSerializerOptions>`* — rejected: would force constructor changes across grains/stores and offers no configurability we actually need.
- *A dedicated `MohistJsonOptions` settings class bound from config* — rejected: there is no requirement to make encoding configurable; adding the knob invites drift, which is the bug we are fixing.

### 2. `JavaScriptEncoder.Create(UnicodeRanges.All)` over `UnsafeRelaxedJsonEscaping`

**Decision:** Configure the encoder as `JavaScriptEncoder.Create(UnicodeRanges.All)`. This passes through all Unicode characters verbatim while still escaping HTML-significant characters (`<`, `>`, `&`, etc.).

**Rationale:** Fixes the garbled text while keeping JSON safe to embed in HTML/script contexts (the framework's default-encoder threat model). Matches the issue's stated safety requirement.

**Alternatives considered:**
- *`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`* — rejected: it also stops escaping HTML-significant characters, reintroducing an XSS surface for any API response rendered into HTML. Not worth the marginal byte savings.

### 3. HTTP wiring: one global `Microsoft.AspNetCore.Http.Json.JsonOptions`

**Decision:** In `ConfigureMohistServices` (alongside `AddSignalR()`), register `Microsoft.AspNetCore.Http.Json.JsonOptions` with `SerializerOptions = JSON.Options`. This covers `Results.Ok`/`Results.Json`, the shared `ApiResults.*` helpers, and the unhandled-exception middleware in one place.

**Rationale:** Minimal-touched ASP.NET Core integration point; removes the need to thread options through each route group. Single registration guarantees every outbound response path shares the encoder.

**Alternatives considered:**
- *Per-route-group `.AddJsonOptions(...)`* — rejected: scattered, recreates exactly the drift this change eliminates.

### 4. SignalR wiring: `.AddJsonProtocol(...)` on the existing `AddSignalR()`

**Decision:** Chain `.AddJsonProtocol(o => o.PayloadSerializerOptions = JSON.Options)` onto the existing `services.AddSignalR()` call. Both hubs (`/hubs/runner`, `/hubs/events`) inherit it automatically.

**Rationale:** `AddSignalR` registers a single protocol for all hubs; configuring it once covers both hubs and all `ITranscriptEventPublisher`/hub pushes.

**Alternatives considered:**
- *Per-hub `JsonHubProtocolOptions`* — rejected: there is no per-hub divergence; one config is correct.

### 5. Custom converter ownership: register options-scoped converters on `JSON.Options`; attribute-based ones need no change

**Decision:**
- `JsonStringEnumConverter` (enum-as-string) stays on `JSON.Options` — already there.
- `UnknownFailureReasonJsonConverter` moves out of `WorkflowQuerier.RunJsonOptions` and is registered once on `JSON.Options` (promoted from private-nested to a shared/internal converter type). Behavior identical; now every path round-trips `FailureReason` the same way.
- `ApprovalFeedbackStatusJsonConverter` is attribute-based (`ApprovalFeedback.cs:6`) and needs no registration — it works under `JSON.Options` unchanged.

**Rationale:** Eliminates the one options-scoped converter that justified keeping a local `RunJsonOptions`. After this, `WorkflowQuerier` (and `IssueQuerier`) can reference `JSON.Options` directly.

**Alternatives considered:**
- *Keep a narrow `RunJsonOptions` for the FailureReason converter* — rejected: defeats single-source-of-truth and re-creates drift; the converter is general-purpose and safe to register globally.

### 6. Eliminate local options fields; converge middle-layer shared options

**Decision:** Replace each local `static readonly JsonSerializerOptions` with `JSON.Options`. The middle-layer shared options classes (`AgentSessionJson.JsonOptions`, `CloudEvent.JsonOptions`, `VariableBundle.JsonOptions`, `WorkflowYamlSerializer.JsonOptions`, `WorkflowVariableJson.Options`) become thin delegators returning `JSON.Options` (or are deleted if only referenced locally). `ConfigService` (human-readable config file) uses `JSON.Indented`.

**Rationale:** All existing local fields are effectively `JsonSerializerDefaults.Web` + `JsonStringEnumConverter` (+/- `DefaultIgnoreCondition.WhenWritingNull`/`PropertyNameCaseInsensitive`, both of which `JSON.Options` already sets), so they are behaviorally subsumed by `JSON.Options` once the encoder is added.

**Migration audit (representative):**

| File | Current field | Target |
|---|---|---|
| `Workflow/Services/WorkflowQuerier.cs:19` | `RunJsonOptions` (+ `UnknownFailureReasonJsonConverter`) | `JSON.Options` (converter promoted to facade) |
| `Workflow/Services/WorkflowQuerier.cs:30` | `StorageJsonOptions` (no enum converter) | `JSON.Options` |
| `Issue/Services/IssueQuerier.cs:22` | `RunJsonOptions` | `JSON.Options` |
| `Infrastructure/Data/Sessions/AgentSessionStore.cs:125` (`AgentSessionJson`) | `JsonOptions` | delegate to `JSON.Options` |
| `Infrastructure/Events/CloudEvent.cs:39` | `JsonOptions` | delegate to `JSON.Options` |
| `Workflow/Domain/VariableBundle.cs:173` | `JsonOptions` | delegate to `JSON.Options` |
| `Workflow/Services/WorkflowYamlSerializer.cs:11` | `JsonOptions` | delegate to `JSON.Options` |
| `Infrastructure/Serialization/WorkflowVariableJson.cs:8` | `Options` | delegate to `JSON.Options` |
| `Project/Domain/ProjectVariablesBag.cs:91` | `JsonOptions` | `JSON.Options` |
| `SystemInfo/SystemUpdateService.cs:69` | `JsonOptions` | `JSON.Options` |
| `Api/AgentDefinitionRoutes.cs:132` / `Api/AgentJobController.cs:261` | `JsonOptions`/`Options` | `JSON.Options` |
| `Workflow/Services/Storage/FileSystem*Storage.cs` | `MetadataJson` | `JSON.Options` |
| `Infrastructure/Events/*EventSerializer.cs` | `JsonOptions` | delegate to `JSON.Options` |
| `Infrastructure/Config/ConfigService.cs` | indented local | `JSON.Indented` |
| `Api/RunnerRoutes.cs:42` | inline `new(JsonSerializerDefaults.Web)` | `JSON.Options` |

(The ~40 default-argument `JsonSerializer.Serialize/Deserialize` call sites — Workflow/Grains, Sessions, ConfigService, Label, Project, Api/LogsRoutes, etc. — switch to `JSON.Serialize`/`JSON.Deserialize` or pass `JSON.Options`.)

**Alternatives considered:**
- *Keep the middle-layer classes as façades over `JSON.Options`* — acceptable fallback for ones with external consumers; prefer deletion where the field is file-local.
- *Leave local fields, just add the encoder to each* — rejected: that is the status quo drift; the bug recurs whenever someone adds a new field without the encoder.

### 7. Encoder is output-only → no persistence migration

**Decision:** Ship without any data/schema migration. Existing SQLite JSON and on-disk config/artifact/session JSON deserialize identically because the decoder accepts both verbatim and `\uXXXX`-escaped input regardless of encoder.

**Rationale:** Verified property of `System.Text.Json`: the encoder governs only serialization. New writes simply become smaller and human-readable.

## Risks / Trade-offs

- **[Risk] `FailureReason` round-trip changes after promoting `UnknownFailureReasonJsonConverter`** → Mitigation: add a golden-master round-trip test (serialize → deserialize → assert equality) covering known and unknown reason values before deleting `WorkflowQuerier.RunJsonOptions`. Spec scenario "Enum conversion behavior is preserved" gates this.
- **[Risk] A local options field had a subtle behavioral difference JSON.Options doesn't cover** → Mitigation: the migration audit table above; each field is `Web` defaults already on `JSON.Options`. Any field that intentionally differs (none found) stays as a documented narrow variant.
- **[Risk] A response path bypasses the global `JsonOptions`** (e.g. a handler writing raw JSON via `JsonSerializer.Serialize` directly into the response) → Mitigation: post-change grep for `JsonSerializer.Serialize` in `Api/` and confirm each passes `JSON.*`; the spec's "no `new JsonSerializerOptions` outside the facade" check catches regressions.
- **[Risk] SignalR clients mis-handle UTF-8 payloads** → Mitigation: both consumers (the TS web client and the .NET runner client) already decode UTF-8 JSON; escaped vs verbatim is wire-equivalent. Covered by manual verification of a hub push with Chinese content.
- **[Trade-off] Verbatim non-ASCII slightly changes byte-for-byte response bodies** → acceptable and intended; no API consumer depends on `\uXXXX` escaping. Field names, structure, and `ApiResponse<T>` are unchanged.

## Migration Plan

**Deploy (single PR, ordered to keep tests green at each step):**
1. Facade: add encoder + `JSON.Indented` to `Infrastructure/JSON.cs`; promote `UnknownFailureReasonJsonConverter` to a shared converter registered on `JSON.Options`.
2. HTTP wiring: register `Microsoft.AspNetCore.Http.Json.JsonOptions` in `ConfigureMohistServices`.
3. SignalR wiring: `.AddJsonProtocol(...)` on `AddSignalR()`.
4. Local-options elimination: migrate fields per the audit table; switch default-arg call sites to `JSON.*`.
5. Add regression tests: non-ASCII verbatim in an API response and a hub payload; `FailureReason`/enum round-trip equality; assert no `new JsonSerializerOptions(` outside the facade (a source-grep test or analyzer).
6. `dotnet test Mohist.sln` green; manual verification (Chinese issue title, Chinese workflow variable, runner event stream).

**Rollback:** Pure revert of the PR. The encoder change is output-direction only — no schema or data was migrated, so previously-written verbatim JSON and older escaped JSON both continue to deserialize. No data-loss path.

## Open Questions

1. **Enforce the "no local options" rule long-term?** Options: a `dotnet test` source-grep test, a Roslyn analyzer, or a CI grep. Lean toward the source-grep test (cheapest, matches the spec's verifiable scenario).
2. **Keep `AgentSessionJson`/`CloudEvent`/`VariableBundle` as thin delegating wrappers, or delete and import `JSON` directly?** Prefer deletion for file-local fields; keep a delegating wrapper only where the type is consumed outside its assembly/area (verify call graphs during implementation).
3. **Should the `Indented` variant be exposed publicly beyond `ConfigService`?** Start with `internal`/facade-internal; widen only if another human-readable file path needs it.
