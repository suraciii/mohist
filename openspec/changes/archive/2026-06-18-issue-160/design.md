## Context

The `mo epic` command group does not exist — `mo epic list` currently falls through to command suggestion ("did you mean repo"). Epic management is Web-UI-only, even though:

- The server-side Epic HTTP surface is complete and stable: `packages/server/src/Mohist.Server/Api/EpicRoutes.cs` maps eight project-scoped endpoints under `/api/projects/{projectRef}/epics` with `ProjectResolutionEndpointFilter` (same pattern as issue routes). DTOs (`EpicCreateRequest`, `UpdateEpicRequest`, `EpicIssueRequest`) are already defined at `EpicRoutes.cs:132-134`.
- The CLI already has a mature, reusable substrate: `MohistCliCommands.cs:10-29` registers peer command groups (`ProjectCommands`, `IssueCommands`, …); `MohistCliApi.cs` exposes `ResolveProjectIdAsync`, `PrintWithOutputAsync`, `PrintPostAsync`, `PrintPatchAsync`, `PrintDeleteAsync`, and a `TableShape` enum + `TableRenderer`; `MohistCliCommands.Issue.cs` is a concrete reference implementation for an 8+ subcommand group.
- The server already emits structured conflict codes that the CLI's generic response printer surfaces as `error (code)`: `DUPLICATE_EPIC_MEMBERSHIP` (`EpicRoutes.cs:77`), `EPIC_NOT_READY_TO_MARK_DONE` (`:119`), `EPIC_ALREADY_TERMINAL` (`:123`), `ISSUE_NOT_FOUND` (`:67`).

Constraint: this is a pure CLI wiring change. Per the issue Non-Goals we MUST NOT touch `EpicRoutes.cs`, the Epic domain model, or Web UI. Risk is `low` — no existing command is affected.

## Goals / Non-Goals

**Goals:**
- Ship a `mo epic` top-level group with 8 subcommands (`list`, `create`, `show`, `update`, `link`, `unlink`, `done`, `close`), each wiring one existing `EpicRoutes.cs` endpoint.
- Match the `mo issue` UX contract: `--project`/`--project-id` project override, `-o table|json` output, table shape via `MohistCliApi.TableShape`, clear empty/error states.
- Make Epic reachable from the CLI independently of Issue (`mo epic show 8` returns Epic #8, not Issue #8).
- Surface conflict responses (`DUPLICATE_EPIC_MEMBERSHIP`, `EPIC_NOT_READY_TO_MARK_DONE`, `EPIC_ALREADY_TERMINAL`) with non-zero exit and no silent success.
- Add CLI integration test coverage for the four mandated cases: list empty/non-empty, create missing title, link duplicate-membership, done not-ready.

**Non-Goals:**
- No server-side change (no `EpicRoutes.cs` edit, no new endpoints, no DTO changes).
- No Epic domain-model change (fields, state machine, membership exclusivity rule unchanged).
- No Web UI change.
- No `mo epic archive` / `reopen` / `start` (no API endpoints; Epics are non-executable containers).
- No fix to the shared issue/epic numbering namespace — only ensure both entities are independently CLI-reachable.
- No priority case-normalization plumbing unless trivially reused from existing issue CLI helpers (see Open Questions).

## Decisions

### D1 — One new file `MohistCliCommands.Epic.cs`, one-line registration

Mirror `IssueCommands` (`MohistCliCommands.Issue.cs:6-36`): an `internal static class EpicCommands { public static Command Build(MohistCliApi api) }` that constructs the `epic` parent `Command` and adds eight `BuildXxx(api)` subcommands. Register at `MohistCliCommands.cs:26` alongside `IssueCommands.Build(api)`:

```
root.Subcommands.Add(EpicCommands.Build(api));
```

A local `ProjectEpicsPath(projectId, path)` helper mirrors `ProjectIssuesPath` (`MohistCliCommands.Issue.cs:40-45`), producing `/api/projects/{escape(projectId)}/epics[/{id}[/...]]`. It throws `InvalidOperationException(NoActiveProjectMessage)` when `projectId` is null — but in practice every subcommand resolves the project first via `api.ResolveProjectIdAsync(...)` and returns `1` on null, identical to the issue flow.

**Alternatives considered:** (a) Extend `IssueCommands` with epic subcommands — rejected, violates separation and bloats a 705-line file. (b) Split into multiple `Epic.*.cs` partials — rejected, 8 small subcommands fit one file comfortably (matches `Issue.cs`).

### D2 — CLI passes `<id|num>` verbatim; the server owns dual-track resolution

`EpicRoutes.cs:35-37, 45-47, 59-61, 86-88, 106-108` already implements the dual-track resolver (`int.TryParse(id)` → `GetByNumberAsync`, else `GetAsync(id)`). The CLI argument is therefore a plain `string` interpolated into the path segment (URL-escaped). The CLI does NOT inspect whether the user passed a number or an `epic_…` id.

This means `mo epic show 8` hits `GET /api/projects/{p}/epics/8`, which the server resolves to Epic #8 — fixing the namespace collision with `mo issue show 8` for free, with zero CLI-side parsing logic.

**Alternatives considered:** (a) CLI normalizes `<id|num>` to a canonical form before sending — rejected, duplicates server logic and creates drift risk; the server is the single source of truth for the resolution contract. (b) CLI exposes separate `--id` and `--number` flags — rejected, worse UX than the documented `<id|num>` positional.

### D3 — Conflict / error codes surface automatically through the existing response printer

`MohistCliApi.PrintResponseAsync` (`MohistCliApi.cs:373-395`) already reads `success`, `error`, and `code` from the envelope and writes `error (code)` to stderr with exit `1` (or `4` for 404). All four Epic conflict/not-found codes are therefore surfaced with no per-command handling. The "no silent success" spec invariant is satisfied by the envelope contract: `success:false` ⇒ non-zero exit.

No special-case branches for `EPIC_NOT_READY_TO_MARK_DONE` etc. are added in `EpicCommands`. If we later want friendlier per-code copy (e.g. listing the undelivered issues using the response's `undeliveredCount`), that is a follow-up enhancement, not required by the spec.

**Alternatives considered:** Per-code `switch` in each write subcommand to print tailored hints — rejected as YAGNI; the generic `error (code)` output is already readable and matches how every other CLI command surfaces API conflicts.

### D4 — `-o table|json` on all 8 commands (acceptance criterion), via three new write-method output helpers

The acceptance criterion requires every subcommand to support `-o table|json`. The existing `PrintWithOutputAsync` (`MohistCliApi.cs:160-178`) is GET-only. The existing `PrintPostAsync` / `PrintPatchAsync` / `PrintDeleteAsync` always emit JSON. To satisfy the criterion without duplicating logic, add three parallel helpers to `MohistCliApi`:

```
Task<int> PrintPostWithOutputAsync(string path, object body, string mode, string? tableShape = null)
Task<int> PrintPatchWithOutputAsync(string path, object body, string mode, string? tableShape = null)
Task<int> PrintDeleteWithOutputAsync(string path, string mode, string? tableShape = null)
```

Each: send the request; on `HttpRequestException` print `ServerUnavailableMessage` and return 1; otherwise branch on envelope — if `success:false` OR `mode==json`, defer to the existing JSON/error printing path (so conflicts still surface as `error (code)`); if `success:true` AND `mode==table`, read `data` and call `RenderTableAsync(data, ParseTableShape(tableShape))`.

This requires a small private unification: factor the "read envelope, decide success, render" body shared with `PrintWithOutputAsync` into a private helper taking an `HttpResponseMessage`. The existing GET method is refactored to call the same helper — net behavior unchanged for all existing commands (guarded by the Issue/Project test suites).

**Mapping of commands → helpers / shapes:**

| Command | HTTP | Helper | TableShape |
|---|---|---|---|
| `list` | GET | `PrintWithOutputAsync` | `EpicList` (new) |
| `show` | GET | `PrintWithOutputAsync` | `EpicShow` (new) |
| `create` | POST | `PrintPostWithOutputAsync` | `EpicShow` |
| `update` | PATCH | `PrintPatchWithOutputAsync` | `EpicShow` |
| `link` | POST | `PrintPostWithOutputAsync` | `EpicMembership` (new) |
| `unlink` | DELETE | `PrintDeleteWithOutputAsync` | `EpicMembership` |
| `done` | POST | `PrintPostWithOutputAsync` | `EpicShow` |
| `close` | POST | `PrintPostWithOutputAsync` | `EpicShow` |

The three new helpers are reusable by future write commands beyond Epic.

**Alternatives considered:** (a) Add `-o` only to `list`/`show`, matching the existing `mo issue create/update` write pattern (writes emit raw JSON) — rejected because the issue's acceptance criterion is explicit that all commands support `-o`, and the proposal echoes it. (b) Special-case epic write commands to ignore `table` mode silently — rejected, violates the "all commands support -o" contract and hurts scriptability.

### D5 — Table shapes: add `EpicList`, `EpicShow`, `EpicMembership` to the enum and `TableRenderer`

Extend `MohistCliApi.TableShape` (`MohistCliApi.cs:180-191`) with three values and add matching `case` arms + private render methods in `TableRenderer.cs` (mirroring `RenderIssueList`/`RenderIssueShow` at `TableRenderer.cs:127-182`):

- `EpicList`: columns `number`, `title`, `status`, `priority` (per acceptance criterion). Empty array ⇒ print a clear `No epics` line (matches `RenderProjectList`'s empty state at `TableRenderer.cs:61-65`).
- `EpicShow`: key/value block — `number`, `title`, `status`, `priority`, `description` (first line, truncated), delivered/total progress, `nextIssue`, and a rendered `linked issues` sub-table (number/title/status). The response DTO already carries these fields via `EpicQuerier` projections.
- `EpicMembership`: one-line confirmation rendering the `{ epicId, issueId }` envelope returned by link/unlink (`EpicRoutes.cs:80, 92`) — e.g. `Linked issue <issueId> to epic <epicId>` / `Unlinked issue <issueId> from epic <epicId>`. Distinguishes link vs unlink via the shape name passed by the caller.

Field-name coupling to `EpicDto` is an accepted risk (see Risks); the CLI renders from `JsonNode` and treats missing fields as empty strings (existing `StringOf`/`NumberOf` helpers at `TableRenderer.cs:400-428`).

### D6 — `--priority` is passed through verbatim; `--description` optional; title required-client-side for `create`

`EpicRoutes.cs:23` enforces `title` is required server-side and returns 400 otherwise. To satisfy the "create missing title fails clearly without calling the API" scenario in the spec, `BuildCreate` validates a non-empty title locally before resolving the project (cheap pre-check; matches the issue CLI's local-validation-first pattern for body sources). `--description` and `--priority` are optional and forwarded as-is in the `EpicCreateRequest`/`UpdateEpicRequest` body.

Priority case normalization (`P2`→`p2`) is NOT added unless trivially reused from an existing issue-CLI helper. The server stores whatever the CLI sends; the acceptance-criterion example uses lowercase `p1` already. See Open Questions.

### D7 — Tests: one new `CliEpicCommandSpecs.cs` using the existing HTTP-recording substrate

Add `packages/cli/tests/Mohist.Cli.Tests/CliEpicCommandSpecs.cs` mirroring `CliProjectCommandSpecs.cs`. Each test wires a `RecordingHttpHandler` (`tests/Mohist.Cli.Tests/Support/RecordingHttpHandler.cs`) with a canned envelope, invokes `MohistCliCommands.RunAsync(http, ["epic", …], output, error, fileSystem, executor)`, and asserts on exit code, emitted request (method/path/body), and output/error text. The four mandated cases:

1. `EpicList_EmptyProject_PrintsEmptyState` / `EpicList_NonEmpty_PrintsTable` — `GET /api/projects/{p}/epics`, assert table headers `number/title/status/priority` and the empty `No epics` line.
2. `EpicCreate_MissingTitle_FailsWithoutCallingApi` — assert exit 1, error on stderr, and `handler.Requests` is empty.
3. `EpicLink_DuplicateMembership_SurfacesConflictCode` — canned `{ success:false, error:"Issue already belongs to epic X", code:"DUPLICATE_EPIC_MEMBERSHIP" }` with HTTP 409; assert exit 1 and stderr contains `DUPLICATE_EPIC_MEMBERSHIP`.
4. `EpicDone_NotReady_SurfacesConflictCode` — canned `EPIC_NOT_READY_TO_MARK_DONE` conflict; assert exit 1 and the code appears on stderr.

Plus a namespace-isolation assertion (`EpicShow_ByNumber_HitsEpicsEndpointNotIssues`) confirming the request path is `/api/projects/{p}/epics/8`, not `/issues/8`.

## Risks / Trade-offs

- **[Field-name coupling to `EpicDto`]** The CLI's `EpicList`/`EpicShow` renderers hard-code JSON field names (`number`, `title`, `status`, `priority`, `deliveredCount`, `totalIssueCount`, `nextIssue`, linked-issue fields). A server-side rename breaks table rendering silently. → Mitigation: the integration test asserts the rendered headers and at least one populated row against a realistic canned DTO, so a rename fails the build. JSON mode is unaffected (verbatim envelope).
- **[Write-mode output helpers touch shared `MohistCliApi`]** D4 factors a private helper shared by GET and write paths. A regression could affect every existing command's output. → Mitigation: the refactor is behavior-preserving and covered by existing `CliProjectCommandSpecs` / issue CLI tests; run the full CLI test suite before merge.
- **[`<id|num>` ambiguity for purely-numeric string ids]** If a future Epic id were ever numeric (e.g. a numeric GUID), the server's `int.TryParse` would treat it as a number. → Mitigation: this is the server's existing contract (`EpicRoutes.cs`), inherited unchanged by the CLI; out of scope for this issue (Non-Goal: no domain-model change). Documented for awareness.
- **[Priority not normalized]** Unlike `mo issue` (which normalizes `P2`→`p2`), epic passes `--priority` verbatim. Users typing `--priority P1` may get unexpected storage. → Mitigation: low (acceptance example uses lowercase; server accepts the value as-is); flagged in Open Questions for a parity follow-up.
- **[No `archive`/`reopen`]** Users may expect symmetry with issue lifecycle. → Mitigation: the API does not expose these endpoints; `mo epic --help` will make the available surface obvious. Out of scope per Non-Goals.

## Migration Plan

This is an additive, low-risk CLI change with no data migration, no server change, and no config change.

**Deploy:**
1. Merge `MohistCliCommands.Epic.cs`, the `MohistCliApi.cs` helper additions, the `TableShape` enum + `TableRenderer.cs` cases, the one-line registration in `MohistCliCommands.cs`, and `CliEpicCommandSpecs.cs`.
2. Build and run `dotnet test` for `packages/cli/Mohist.Cli.Tests` and any solution-level test target. Confirm all existing CLI tests still pass (guarding the D4 refactor).
3. Ship via the normal CLI release channel; no server restart required.

**Rollback:** Revert the merge commit. Existing commands are untouched, so rollback is clean and complete — no state to clean up (the CLI stores no Epic state locally; everything is server-owned).

**Validation smoke test after deploy:** `mo epic list`, `mo epic show <existing-epic-number>`, `mo epic create "Test" --priority p2`, `mo epic done <undelivered-epic>` (expect `EPIC_NOT_READY_TO_MARK_DONE`).

## Open Questions

1. **Priority case normalization** — Should `--priority P1` be normalized to `p1` for UX parity with `mo issue` (REQ: "Issue CLI normalizes touched priority inputs")? Recommend: yes, reuse the existing issue-CLI normalization if it is extractable cheaply; otherwise pass through verbatim and file a follow-up. Needs a quick check of where issue-CLI priority normalization currently lives.
2. **`EpicShow` linked-issues sub-table shape** — The Epic detail DTO includes a linked-issues collection. Should the table mode render it as a nested table (full number/title/status) or a compact count? Recommend nested table for parity with `RenderIssueShow` richness, pending confirmation of the exact `EpicDto` linked-issue field name (`issues` vs `linkedIssues`).
3. **`--description` from stdin/file** — `mo issue create` supports `--body @file` / `--body -`. Should `mo epic create --description @file` get the same treatment? Recommend: no for v1 (descriptions are short; the issue Non-Goals discourage scope creep), but worth confirming the spec author did not intend full parity.
