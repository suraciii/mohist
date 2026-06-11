# Design — Issue #83: Expand Mohist CLI ergonomics for project-scoped work

## Context

The Mohist CLI (`packages/cli/Mohist.Cli/`) is the surface that agents and operators use to inspect and act on project-scoped issues. The current shape (`MohistCliCommands.cs`, `MohistCliCommands.Issue.cs`, `MohistCliCommands.Project.cs`, `MohistCliApi.cs`) has four implementation-oriented gaps that block safe multi-project CLI workflows:

1. **Project selection is id-only.** Every project-scoped `mo issue` subcommand exposes only `--project-id` (`MohistCliCommands.ProjectIdOption()` in `MohistCliCommands.cs:41`). Users and agent prompts have to know the internal `proj_…` id, which is what `mo project use` resolves *to* but is not what users think in. The server already accepts name-or-id on `/api/projects/{projectRef}` via `ProjectResolutionEndpointFilter` (see `IssueRepositoryResolutionHelpers.cs`).
2. **Output is JSON-only.** `api.PrintResponseAsync` (`MohistCliApi.cs:142`) always pretty-prints the `data` field. There is no human-friendly view for the commands humans most often run (`project list/show`, `issue list/show`, `issue workflow status`, `issue sessions`).
3. **`issue create` / `issue update` only accept inline `--body`.** `BuildCreate` (`MohistCliCommands.Issue.cs:88`) and `BuildUpdate` (`MohistCliCommands.Issue.cs:155`) define a single `--body` string. Long Markdown bodies and agent-generated content break under shell quoting or get truncated by callers.
4. **No CLI surface for project repositories.** `ProjectRoutes.cs:82-128` already exposes `GET/POST/PATCH/DELETE /api/projects/{ref}/repositories`, but `mo project` only has `list/create/show/use/delete` (`MohistCliCommands.Project.cs:6-19`). Operators have to drop to raw HTTP.

The server-side repository API and the `ProjectResolutionEndpointFilter` are stable, so this issue is a **purely additive CLI ergonomics pass** that must preserve full backwards compatibility for existing scripts.

Stakeholders:
- **Agents** — need `--project mohist-local` and stdin/file body input to drive long-form workflows safely.
- **Human operators** — need `--output table` to scan list/detail commands and `mo project repo` to manage repositories.
- **Existing scripts and prompts** — must keep working unchanged (notably `--project-id` and the JSON default).

## Goals / Non-Goals

**Goals:**
- Add a canonical `--project <name-or-id>` to every project-scoped `mo issue` subcommand and `mo project repo`; keep `--project-id` as a backwards-compatible alias.
- Resolve both options to a single project id; if both are passed with **different** resolved values, fail fast with a clear error instead of silently picking one.
- Preserve active-project fallback (`mo project use`) when neither option is passed.
- Standardize the "no active project" diagnostic to `Run 'mo project use <name-or-id>' or pass --project <name-or-id>`.
- Add `--output table|json` (default `json`) to `project list/show`, `issue list/show`, `issue workflow status`, `issue sessions`, and `project repo list`.
- Extend `issue create`/`issue update` with `--body-file <path>` and `--body-stdin`; the three body sources are mutually exclusive and one is required.
- Add `mo project repo {list, add, set-default, remove}` wrapping the existing server endpoints; no new server routes or schemas.

**Non-Goals:**
- No changes to server domain models, routes, or workflow engine semantics.
- No removal of `--project-id` (compatibility alias).
- No new issue commands (comments, search, pagination, advanced filters).
- No new "output formats" beyond `table` and `json` (e.g. no YAML, no CSV).
- No streaming/interactive UI; table output is plain text via the existing `TextWriter` output.

## Decisions

### D1. One shared `ProjectRefOption()` helper for `--project` + `--project-id`

**Choice.** Add `MohistCliCommands.ProjectRefOption()` that returns two `Option<string?>` instances wired to the same `Description` text: `--project` (canonical, marked first in help) and `--project-id` (alias, marked "backwards-compatible alias"). Both are added to every project-scoped command's `Options`. The existing `ProjectIdOption()` is kept as a thin wrapper for any non-issue call sites so we don't break the build, but every issue/project-repo call site switches to the new helper.

**Rationale.** Specs `cli-project-ref` and `cli-interface` require that `--project` and `--project-id` be the **same option semantically** (same-value ok, different-value error, identical request path). The cleanest way to guarantee that is to have a single helper that produces both options and a single resolver that consumes both. Wiring it manually at 20+ call sites would invite drift.

**Alternatives considered.**
- *Single `Option<string?>` registered under two aliases* — `System.CommandLine` does not let one option instance be referenced by two aliases that show up as separate `--` flags in `--help`. We would lose the ability to print both names in help.
- *Subclass `Option` with custom parsing* — over-engineered; we don't need to merge values, only validate them.
- *Per-call-site handling* — works but bloats every action delegate and makes the conflict rule easy to forget.

### D2. `ResolveProjectIdAsync` accepts a `(project, projectId)` tuple and runs a same-value / different-value check

**Choice.** Change `MohistCliApi.ResolveProjectIdAsync` from `string? explicitProjectId` to `ResolveProjectIdAsync(string? project, string? projectId)`. The resolver:
1. If both are blank, fall back to `cli-state.json`'s `activeProjectId`.
2. If exactly one is non-blank, use it (server resolves name→id).
3. If both are non-blank and identical (string-equal), use the single value.
4. If both are non-blank and different, **do not call the server**: print the guided error to stderr and return a sentinel that the action delegate maps to a non-zero exit code.

The same-value / different-value check is intentionally a **string compare**, not a server round-trip. Reasons:
- If the user passes the same value twice (`--project foo --project-id foo`), they want it to "just work"; forcing a server call to confirm introduces latency and a new failure mode.
- If the user passes two different values, both could individually resolve; the right behavior is "tell the user, don't pick." A string compare is sufficient and predictable.
- The server already enforces uniqueness on project names, so two values that happen to be string-equal after URL-encoding must refer to the same project.

**Alternatives considered.**
- *Always server-round-trip and compare ids* — adds an extra request on the happy path; breaks the no-network-on-validation-error rule (`cli-body-input-sources` requires that a body-source conflict "does not make a server request", and we want the same property for project-ref conflicts).
- *Always reject when both are passed* — would break the explicit "same value ok" scenario from `cli-project-ref`.

### D3. Standardized "no active project" diagnostic, rendered from one helper

**Choice.** Replace the hard-coded string in `MohistCliCommands.Issue.cs:43` (`"No active project. Run 'mo project use <id-or-name>' or pass --project-id."`) with a single helper `MohistCliCommands.NoActiveProjectMessage` that returns `Run 'mo project use <name-or-id>' or pass --project <name-or-id>`. The helper is also used by `MohistCliApi.ResolveProjectIdAsync` so the new `mo project repo` subcommands emit identical wording. The `--project-id` form is **intentionally not** mentioned in the diagnostic; it still works as an alias but the canonical option is taught to the user.

**Rationale.** `cli-interface` requires that "Diagnostic wording is consistent across commands" and that the diagnostic references the canonical `--project` option.

### D4. `OutputOption()` shared helper for the seven `--output`-enabled commands

**Choice.** Add `MohistCliCommands.OutputOption()` that returns a single `Option<string>` with `DefaultValueFactory = _ => "json"` and a description that documents the accepted values. Each of `project list/show`, `issue list/show`, `issue workflow status`, `issue sessions`, and `project repo list` adds this option. Each command's action calls a new `MohistCliApi.PrintWithOutputAsync(path, mode)` that, after fetching the response, dispatches to either the existing `PrintResponseAsync` (json) or a new `RenderTableAsync(data, shape)` (table). Unknown values are validated up-front by the action delegate before any HTTP call.

**Rationale.** `cli-output-modes` requires that unknown values "SHALL NOT make a server request", so validation must happen pre-flight. Centralizing the option also keeps help text identical.

**Alternatives considered.**
- *Per-command `Option<string>` with the same description, no helper* — would work but the description and default value would drift across calls.
- *Use a custom binding to a `OutputMode` enum* — cleaner typing but `System.CommandLine` enum parsing emits its own error message that doesn't match the spec's "list the accepted values" wording. We validate manually in the action delegate.

### D5. Table rendering is a presentation-time concern over the same `data` payload

**Choice.** Add a new `MohistCliApi.RenderTableAsync(JsonNode? data, TableShape shape)` that takes the already-decoded `data` JSON node and a `TableShape` enum (`ProjectList`, `ProjectShow`, `IssueList`, `IssueShow`, `WorkflowStatus`, `Sessions`, `RepoList`). The shape determines which columns are rendered and how text is truncated. The HTTP request and response body are identical to the JSON path. There is **one** server round-trip per command regardless of mode.

**Rationale.** `cli-output-modes` explicitly requires that "Table rendering does not require extra server round-trips" and that "Table and json hit the same endpoint." Doing all rendering from the existing `data` payload keeps the contract simple and means the JSON path is unchanged. Column shapes are listed in the spec's scenarios (e.g. project list: `id | name | base branch | *active*`).

**Alternatives considered.**
- *Send `Accept: text/table` to the server and have the server render* — would couple presentation to the server, conflict with the "stable automation format = JSON" rule, and require a new content-type negotiation story.
- *Re-fetch the data through a second API call* — extra latency; spec forbids it.

### D6. Body input resolution is a dedicated `BodyInputResolver` static class

**Choice.** Introduce `internal static class BodyInputResolver` in `Mohist.Cli` with one method:

```csharp
public static async Task<string> ResolveAsync(
    string? inlineBody,
    string? bodyFile,
    bool bodyStdin,
    IFileSystem fileSystem,
    TextReader standardInput,
    TextWriter error);
```

The method enforces:
- Exactly one source required (zero → "issue body is required" error, exit code 1, no HTTP call).
- More than one source → mutual-exclusion error listing the conflicting options, exit code 1, no HTTP call.
- File read uses `_fileSystem.ReadAllTextAsync` (already abstracted for tests) and UTF-8. A missing/unreadable file produces a "could not read body file: …" error, exit code 1, no HTTP call.
- Stdin is drained to EOF (TextReader is passed in for testability — production passes `Console.In`).
- The resolved string is sent in the JSON body via the existing `PrintPostAsync`/`PrintPatchAsync`; we never log the body in non-debug mode (the current code does not log bodies either, so this is the same behavior).

`BuildCreate` and `BuildUpdate` are updated to:
1. Register the new options with mutual-exclusion-aware help text.
2. Call `BodyInputResolver.ResolveAsync` before constructing the request object.
3. Pass the resolved string as `body` in the create/update payload.

**Rationale.** `cli-body-input-sources` requires that the resolution happen "before the create or update request is constructed" and that a "missing body source fails with a clear error and non-zero exit" **and** "does not make a server request." Centralizing in a static class makes the rules testable in isolation (we can write `FakeFileSystem` + fake `TextReader` tests) without going through `System.CommandLine`).

**Alternatives considered.**
- *Inline the logic in each `Build*` method* — duplicates ~30 lines of validation in two places; easy to forget the "no HTTP on error" rule in one.
- *Use `Option<>` validators (CustomValidator) in `System.CommandLine`* — validators do run before the action, but they can't easily access stdin / file system without injecting them, and the error messages they emit are routed differently from the action's error writer.

### D7. `mo project repo` is a sibling subcommand group, not nested in `mo project issue`

**Choice.** Add `BuildRepo` returning a new `Command("repo", "Manage project repositories")` and register it under `ProjectCommands.Build`. The four subcommands are:

| Subcommand | Server endpoint | Method |
|---|---|---|
| `list` | `/api/projects/{ref}/repositories` | GET |
| `add --name X [--path Y] [--remote Z] [--base-branch W] [--set-default]` | `/api/projects/{ref}/repositories` | POST with `AddRepositoryRequest` |
| `set-default <name>` | `/api/projects/{ref}/repositories/{repoName}` | PATCH with `{ setDefault: true }` |
| `remove <name>` (alias `rm`) | `/api/projects/{ref}/repositories/{repoName}` | DELETE |

Only `list` accepts `--output` (per `cli-project-repositories`); `add`/`set-default`/`remove` always print success/failure text. All four use the same `ProjectRefOption()` + `ResolveProjectIdAsync` flow as issue commands, so the active-project fallback and "no active project" diagnostic are inherited.

**Rationale.** `cli-project-repositories` requires that the four subcommands wrap the existing server endpoints. Naming the argument `name` for the subcommands that take a repo name matches the `name` field of `AddRepositoryRequest` (server-side validation: `name is required` at `ProjectRoutes.cs:90`).

**Alternatives considered.**
- *Reuse `MohistCliApi.PrintPostAsync` with the existing `JsonOptions`* — yes, this is what we do; no new client serializer.
- *Add server-side CLI shortcut endpoints* — explicitly out of scope; spec says "no new server route, schema, or grain method."

### D8. Tests live next to the existing `ProjectCliSpecs` harness

**Choice.** New spec files under `packages/server/tests/Mohist.Server.Tests/Specs/Api/`:

- `IssueCliProjectRefSpecs.cs` — name resolves, id resolves, both resolve to the same issue, `--project-id` alias still works, conflicting options fail, active project fallback, no-active-project diagnostic, applied to ≥3 subcommands (`show`, `list`, `sessions`).
- `IssueCliOutputModeSpecs.cs` — `issue list/show/workflow status/sessions` with `--output table|json`, default is json, unknown value fails pre-flight, request identical between modes.
- `IssueCliBodyInputSpecs.cs` — `--body` / `--body-file` / `--body-stdin` happy paths on both `create` and `update`, mutual exclusion, missing source, missing file.
- `ProjectCliRepositorySpecs.cs` — `list` / `add` / `set-default` / `remove` request paths and payloads; conflict/not-found error surfacing; `--output` only on `list`.

All four reuse the existing `RecordingHttpHandler` + `FakeFileSystem` + `MohistCliCommands.RunAsync` pattern from `ProjectCliSpecs.cs:113`. The test harness already fakes stdin via a `TextReader` we pass to `RunAsync` (see how `MohistCliApi` is constructed at `MohistCliCommands.cs:57-76` — we add a `TextReader standardInput` parameter to `RunAsync` so `BodyInputResolver` can be tested without real `Console.In`).

**Rationale.** Specs explicitly require "CLI command tests cover project-name selection, backwards-compatible `--project-id`, body input source validation, output mode validation, and repository command request paths." The existing harness covers HTTP recording and file system fakes; we extend it minimally with a stdin parameter.

**Alternatives considered.**
- *New `Mohist.Cli.Tests` project* — would duplicate the `RecordingHttpHandler` and the DI wiring; the project already references `Mohist.Cli` from `Mohist.Server.Tests` (the `ProjectCliSpecs` tests prove this), so we follow the existing pattern.

## Risks / Trade-offs

- **`--project` and `--project-id` are two options in `System.CommandLine`, not one with two aliases.** If both are passed with the same value, users will see both flags in help; if a user types `--project foo --project-id bar` expecting "the later one wins", the spec requires a hard error instead. → **Mitigation:** the help text on `--project` says "canonical", the help on `--project-id` says "backwards-compatible alias"; the conflict error wording (`--project` and `--project-id` resolve to different values; pass only one) is explicit. The validation lives in `ResolveProjectIdAsync` so every call site is covered.
- **String-equality on the same-value check** doesn't catch the case where one flag is `mohist-local` and the other is the resolved `proj_…` id. We consider this acceptable: the spec's example shows both options as raw strings that happen to match. If a user passes `--project mohist-local --project-id proj_…` where `proj_…` happens to be a different project's id, the string check will (correctly) fail with the conflict error. → **Mitigation:** the error message always lists both values, so the user can see the discrepancy.
- **Table rendering is hand-rolled and must stay in sync with the server response shape.** If the server renames `id` → `projectId` on a payload, the table breaks silently. → **Mitigation:** `TableShape` is enum-driven and the column accessors (`data["id"]`, `data["name"]`, …) are co-located in one file; a smoke test (`ProjectCliRepositorySpecs`/`IssueCliOutputModeSpecs`) asserts that at least one representative cell renders, so a total breakage shows up in CI.
- **Mutual exclusion of body sources is enforced in the resolver, not by `System.CommandLine`.** A user who passes `--body x --body-file f` will see the resolver's error, not a `System.CommandLine` parse error. The wording matches the spec ("`--body` and `--body-file` are mutually exclusive"). → **Mitigation:** the resolver is the single source of truth; `BuildCreate`/`BuildUpdate` are the only call sites.
- **Adding `--project` to ~20 subcommands is a large surface change.** → **Mitigation:** `ProjectRefOption()` returns the two options; we replace `ProjectIdOption()` at each site with one line (`cmd.Options.AddRange(MohistCliCommands.ProjectRefOption())`), keeping the diff mechanical and reviewable.
- **`mo project repo set-default` uses PATCH with `{ setDefault: true }`.** If the server's `UpdateRepositoryRequest` grows new actions in the future, the CLI will need a corresponding option. → **Mitigation:** the spec already says the server surface is stable; the CLI's `--set-default` option is described as "currently the only action", and we accept a "BadRequest: No action specified" error from the server if a future flag is added without CLI support.

## Migration Plan

This change is **additive and backwards compatible**. Deployment is the normal Mohist CLI release:

1. Land the change behind the existing `mohist` dev cycle (build → unit tests → smoke). No feature flag needed; the new options are opt-in.
2. Update the user-facing docs (if any) and the `mo issue --help` / `mo project --help` strings.
3. Communicate the new options in the next release notes: `--project <name-or-id>`, `--output table`, `--body-stdin` / `--body-file`, `mo project repo {list, add, set-default, remove}`.

**Rollback.** Each of the four feature areas is independently revertable:
- Revert `ProjectRefOption()` and restore the previous `ProjectIdOption()` call sites → `--project` disappears, `--project-id` keeps working.
- Revert `OutputOption()` and the new `PrintWithOutputAsync` path → output is JSON-only.
- Revert `BodyInputResolver` and the new options on `create`/`update` → only inline `--body` is accepted.
- Revert `BuildRepo` → `mo project repo` subcommands disappear; server endpoints are untouched.

No server-side rollback is required: the four server repository endpoints are unchanged and the new CLI commands wrap them without introducing new server state.

## Open Questions

- **Should `--project` be allowed on `mo project use` / `mo project show` as well?** Specs list only the `mo issue` subcommands and the new `mo project repo` group. `mo project show` already takes a positional `project` argument (`MohistCliCommands.Project.cs:51`); adding `--project` there is redundant. **Decision: leave `mo project` subcommands as-is** unless a follow-up issue asks for it.
- **Should `mo project repo add` accept the repository payload as a JSON file (`--from-file`)?** Specs don't require it; `--name` / `--path` / `--remote` / `--base-branch` / `--set-default` flags are sufficient. **Decision: defer** to a follow-up issue if needed.
- **Color in table output?** Tabled text is currently uncolored; coloring is a future ergonomic improvement and out of scope here. **Decision: plain text only.**
- **Table column widths and truncation policy?** The spec says "truncate to a reasonable terminal width" without a specific number. **Decision:** use a soft cap of 60 chars on title/body-like fields and 24 on id-like fields, single-line truncation with `…`. A future spec can pin this down.
