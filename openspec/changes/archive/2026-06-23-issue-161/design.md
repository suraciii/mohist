## Context

The issue CLI (`packages/cli/Mohist.Cli/`) is a C# thin client built on `System.CommandLine`. The server (`packages/server/`) is ASP.NET Core + Orleans. Issue CRUD lives in `IssueRoutes.Crud.cs`; the PATCH handler deserializes an `UpdateIssueRequest` DTO and passes it to the `IssueGrain` via `UpdateFullAsync(UpdateIssueData)`.

**Current PATCH path** (`IssueRoutes.Crud.cs:95-130`): ASP.NET model binding deserializes the JSON body into `UpdateIssueRequest`. All optional fields are nullable reference types — when a JSON key is absent or explicitly `null`, the DTO field is `null`. The handler cannot distinguish "user omitted this field" from "user explicitly sent null to clear it." The domain `Issue.Update()` (`Issue.Transitions.cs:45-81`) treats `null` as "skip" for title/body/labels/priority. `IsDraft` uses `HasValue` check. There is no way to explicitly *clear* labels via PATCH.

**Current CLI update path** (`MohistCliCommands.Issue.cs:305-423`): Builds a static anonymous object that always includes every field key — even when the user didn't provide the flag, the key is present with a `null` value. For example, `mo issue update 42 --body-file f.md` sends `{"title":null,"body":"...","labels":null,...}`. While the domain currently skips null labels, this is fragile and ambiguous. There is no path to explicitly clear labels through the CLI.

**Raw-presence pattern already exists**: `AgentDefinitionRoutes.cs:134-159` implements `BindAsync` with raw `JsonElement` parsing and `TryGetProperty` to detect which top-level keys the client actually sent. A `Fields` set records presence. This is the proven pattern to extend to issue PATCH.

**All server endpoints for new CLI subcommands already exist**: prerequisites (`IssueRoutes.Prerequisites.cs`), comments (`IssueRoutes.Lifecycle.cs:62-89`), feedback (`IssueRoutes.Feedback.cs:13-42`), reject (`IssueRoutes.WorkflowControl.cs:42-57`), stop (`IssueRoutes.WorkflowControl.cs:131-150`).

## Goals / Non-Goals

**Goals:**
- Make PATCH "omit means unchanged" unambiguous and enforceable for all optional fields (labels, isDraft, attachmentIds) via raw-body presence detection on the server.
- Make `mo issue update` send only the fields the user explicitly provided.
- Add `--repository`, `--stage-models`, `--stage-model-variants` flags to `mo issue create`; add `--stage-models`, `--stage-model-variants` to `mo issue update`.
- Wire six new CLI write verbs to existing server endpoints: `prereq add/remove`, `comment add`, `feedback create`, `reject`, `stop`.

**Non-Goals:**
- No new server endpoints.
- No issue domain model changes (state machine, invariants, priority semantics).
- No Web UI changes.
- No `Issue.Risk` changes (label-class, deferred to epic #8 / #149).
- No `mo issue workflow config` or `mo issue session` CLI (follow-up issues).
- No `mo issue label add/remove` (labels still go through `mo issue update --label`).

## Decisions

### D1: Server PATCH uses `BindAsync` + `JsonElement` raw-body parsing

**Decision**: Replace `UpdateIssueRequest` DTO model binding with a custom `BindAsync` that parses the raw body as `JsonElement`, exactly like `AgentDefinitionRoutes.AgentUpdateRequest.BindAsync`. The parsed request carries both the deserialized values and a set of present field names.

**Rationale**: This is the established pattern in the codebase. `TryGetProperty` cleanly distinguishes absent vs present-null vs present-value. No new dependencies, no middleware.

**Alternative considered**: A JSON merge-patch (RFC 7396) middleware that transforms the body before model binding. Rejected: adds infrastructure for a single endpoint; the `BindAsync` pattern is localized and proven.

**Implementation**: The `UpdateIssueRequest` gains a `Fields` set (like `AgentUpdateRequest`). The grain's `UpdateFullAsync` receives a new `UpdateIssueData` shape or the handler passes presence info separately. The domain `Issue.Update()` method gains explicit "clear" support for labels (calling `ReplaceLabels` with an empty map when the field is present-null), and `attachmentIds` null handling is made explicit (absent = keep, null = unbind all, value = replace).

### D2: CLI builds PATCH body as a dynamic dictionary

**Decision**: Replace the static anonymous object in `BuildUpdate` with a `Dictionary<string, object?>` that only adds keys for flags the user explicitly provided.

**Rationale**: Makes the CLI a correct client of the server's raw-presence-aware contract. Eliminates the ambiguous `labels: null` sent when `--label` is omitted. Also reduces request payload size.

**Alternative considered**: Continue sending all fields as null and rely on server to skip. Rejected: this makes "clear labels" impossible and contradicts the new three-state contract.

**Implementation**: Each flag is checked: if the user provided `--title`, add `title` to the dictionary; if `--label` was passed, resolve and add `labels`; etc. The existing label-merge logic (fetch current labels, apply delta) is preserved when `--label` is provided. The dictionary is serialized as the PATCH body.

### D3: New CLI subcommands follow `BuildAction` / `BuildFeedback` patterns

**Decision**: Wire `prereq add/remove`, `comment add`, `feedback create`, `reject`, `stop` using the existing command-building patterns already in `MohistCliCommands.Issue.cs`.

| Subcommand | Pattern | Body input |
|---|---|---|
| `prereq add <num> <prereq-num>` | `BuildAction` + positional arg | `{"prerequisiteNumber": N}` |
| `prereq remove <num> <prereq-num>` | New DELETE command | no body |
| `comment add <num>` | `BuildFeedbackList` style + body | `--body`/`--body-file` via `BodyInputResolver` |
| `feedback create <num>` | Extends `BuildFeedback` group | `--stage` required + `--body`/`--body-file` |
| `reject <num>` | `BuildAction` + `--message` flag | `{"message": "..."}` |
| `stop <num>` | `BuildAction` clone | no body |

**Rationale**: Every new verb is a thin HTTP call to an existing endpoint. No business logic in the CLI. The `BodyInputResolver` class already handles `--body`/`--body-file`/`--body-stdin` resolution and is reused for `comment add` and `feedback create`.

**`@file` JSON for stage-models**: A small helper (alongside `BodyInputResolver`) reads a file when the value starts with `@`, parses it as JSON, and returns the object. This is only needed for `--stage-models` / `--stage-model-variants`, not for body text.

### D4: Create/update execution config flags pass through DTO fields

**Decision**: Add `--repository`, `--stage-models`, `--stage-model-variants` options to `BuildCreate` and `--stage-models`, `--stage-model-variants` to `BuildUpdate`. Include these in the request body when provided.

**Rationale**: The `CreateIssueRequest` and `UpdateIssueRequest` DTOs already declare these fields (`Model`, `StageModels`, `AgentConfig`, `WorkflowProfileId`). The CLI create command already sends `model` and `workflowProfileId`. Adding `stageModels`, `stageModelVariants`, and `repository` is the same pattern.

## Risks / Trade-offs

- **[Model metadata fields may not be wired server-side]** -> The `CreateIssueRequest` DTO declares `Model`, `StageModels`, `AgentConfig`, `WorkflowProfileId` but the POST handler (`IssueRoutes.Crud.cs:59-70`) does NOT pass these to `issueGrain.CreateAsync()`. The issue body claims "no server model-writing changes needed," but the code shows these fields are currently dropped. **Mitigation**: Verify during implementation whether model metadata flows through a separate path (e.g., `IssueWorkflowProfileManager`). If not, wiring these through the grain is a prerequisite for the B-category acceptance criteria. Flag as open question O1.

- **[PATCH handler `BindAsync` requires request body buffering]** -> Custom `BindAsync` reads `HttpContext.Request.Body`. ASP.NET Core allows this once; the body stream is not rewindable by default. **Mitigation**: `BindAsync` is the single consumer of the body for that request. No downstream middleware needs to re-read it. This is the same approach `AgentDefinitionRoutes` uses without issue.

- **[Domain `Issue.Update()` currently can't clear labels]** -> The domain method treats `null` labels as skip. Supporting "present-null = clear" requires either a new domain method or a sentinel. **Mitigation**: The grain handler interprets raw-presence and calls the appropriate domain method — `Update()` for value-replace, a new `ClearLabels()` or `ReplaceLabels(empty)` for explicit clear, and skips entirely for absent. This keeps three-state logic in the grain handler, not the domain.

- **[CLI `prereq remove` needs DELETE with project path]** -> The existing `apiClient` helpers focus on GET/POST/PATCH. **Mitigation**: Add a `PrintDeleteAsync` helper to `MohistCliApi`, following the same pattern as `PrintPostAsync`.

## Migration Plan

This is a backward-compatible change with no data migration:

1. **Server PATCH fix** (A): Deploy first. Old clients sending `labels: null` will be treated as "labels key present, value null = clear." This is a behavior change for clients that relied on `null` meaning "skip." However, the current Web UI and CLI are the only consumers, and the CLI is updated in the same deployment.

2. **CLI update** (A+B+C): Deploy alongside or after server. The CLI stops sending absent fields, so it works correctly with both old and new server behavior.

3. **Rollback**: Revert both server and CLI changes. The old behavior (null = skip at domain level) is restored. No data is affected.

## Open Questions

- **O1**: The issue body states the server already persists `Model`/`StageModels`/`StageModelVariants` via `IssueWorkflowProfileManager` and no server model-writing changes are needed. However, `IssueRoutes.Crud.cs` create handler (line 59-70) does not pass `req.Model`, `req.StageModels`, or `req.WorkflowProfileId` to `issueGrain.CreateAsync()`, and the PATCH handler does not reference these fields at all. Are these fields applied through a middleware/filter or a different code path not found in `IssueRoutes.*.cs`? If not, the server needs a small wiring change to pass these to the grain/profile manager — contradicting the issue's "no server changes for model" claim. This must be resolved before implementing B-category flags.

- **O2**: The `stop` endpoint comment (`IssueRoutes.WorkflowControl.cs:129-130`) says "the issue itself is NOT closed; the user can re-open or close it separately." Should `mo issue stop` output guide the user to also close the issue, or is the workflow-only stop sufficient?
