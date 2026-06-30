## Context

The Mohist server already exposes the full project-level workflow configuration surface (workflow-template CRUD, default-template, variables merge/replace, prompt overrides) and the issue-session followup endpoint, but the `mo` CLI only exposes the **issue-level** workflow config (`mo issue workflow config`) and the **generic agent-session** followup (`mo agent session followup`). A CLI/script-driven user who wants to configure a project's workflow, push a followup into a running issue workflow session, or batch-archive completed issues must fall back to Web UI or raw `curl` — even though these are active *functional entry points* (configuration, triggering, state change), not passive display surfaces.

The CLI is a .NET 11 System.CommandLine tree in `packages/cli/Mohist.Cli/`. Commands are grouped into static classes per area (`ProjectCommands` in `MohistCliCommands.Project.cs`, `IssueCommands` in `MohistCliCommands.Issue.cs` ~2100 lines, `AgentCommands` in `MohistCliCommands.Agent.cs`). All HTTP goes through one client, `MohistCliApi` (`MohistCliApi.cs`), whose `Print*WithOutputAsync` family handles the `{success,data,error,code}` envelope, table-vs-json rendering, and generic error surfacing (`error (code)` to stderr; exit `4` for 404, else `1`). Project resolution is centralised in `api.ResolveProjectIdAsync`. Output formatting dispatches through `partial class TableRenderer` keyed on `MohistCliApi.TableShape`.

Three relevant precedents this design mirrors directly:

1. **`mo issue workflow config {get,set,clear,preview}`** (`IssueCommands.BuildWorkflowConfig`, `MohistCliCommands.Issue.cs:1122`) — the exact composite-flag organisation (`--template`/`--var`/`--stage-var`/`--prompt`, `@file` expansion, "provided"-detection via `IsOptionProvided`, per-category independent requests) the new project-level commands will copy.
2. **`mo agent session followup`** (`AgentCommands.BuildSessionFollowup`, `MohistCliCommands.Agent.cs:450`) — the `--text`/`--text-file`/`--text-stdin` triple resolved by `BodyInputResolver.ResolveAsync`, generic error path that already surfaces `session_inactive`/`runner_offline`, and the `TableShape.AgentSessionFollowup` renderer printing `delivery: sent`.
3. **`mo issue archive --all-completed`** (`IssueCommands.BuildArchive`, `MohistCliCommands.Issue.cs:749`) — the flag and `POST /issues/archive-completed` call **already exist and ship**, but are undocumented in `docs/cli-reference.md` and have zero test coverage.

Constraints: no server API/contract/domain changes (every endpoint is live in `ProjectRoutes.cs` / `IssueRoutes.Sessions.cs` / `IssueRoutes.Lifecycle.cs`); no Web UI changes; the `cli-interface` is the only capability touched.

Stakeholders: CLI/script-driven users (the user voice); the mohist skill layer that drives the CLI.

## Goals / Non-Goals

**Goals:**

- Add a `mo project workflow` subgroup (`template` CRUD + `config` get/set/clear/preview) that is symmetric with `mo issue workflow config` and exposes the project-only full-replace variable path.
- Add `mo issue session followup <num> <name>` as a 5th verb under the existing `mo issue session` group, faithfully surfacing `session_inactive` / `runner_offline`.
- Formalise `mo issue archive --all-completed` with documentation and tests so it meets the bar of the other verbs.
- Reuse existing helpers (`BodyInputResolver`, `Print*WithOutputAsync`, `ResolveProjectIdAsync`, `IsOptionProvided`, `ExpandAtFileAsync`, existing table renderers) rather than inventing parallel machinery.
- Add CLI command specs using the existing `RecordingHttpHandler` / `FakeFileSystem` / `FakeCommandExecutor` harness.

**Non-Goals:**

- No server endpoint, contract, or domain change.
- No Web UI change.
- No metrics / inbox / agent-ops / read-only-browse commands (display-only surfaces, excluded by the entry-point-vs-display invariant).
- No change to `mo issue workflow config` (issue-level) or `mo agent session followup` (generic AgentSession).
- No redesign of `BodyInputResolver`, `MohistCliApi`, or `TableRenderer` dispatch.

## Decisions

### D1. Mirror the issue-level workflow-config command shape for `mo project workflow`

Add a new `internal static class ProjectWorkflowCommands` in a new file `MohistCliCommands.ProjectWorkflow.cs`, wired into `ProjectCommands.Build` via `project.Subcommands.Add(ProjectWorkflowCommands.Build(api))`. It registers a `workflow` command with two subgroups:

- `template` → `list|create|show|update|delete` over `GET/POST/PUT/DELETE /api/projects/{id}/workflow-templates[/{tid}]`.
- `config` → `get|set|clear|preview` over `/api/projects/{id}/workflow-profile[/...]`.

**Rationale:** the issue-level `BuildWorkflowConfig` already solved the composite-flag UX (`--var`/`--stage-var`/`--prompt`, `@file`, provided-detection, per-category independent requests). Copying that shape gives users a symmetric mental model and lets us reuse `PrintWorkflowProfileAsync`-style helpers, `IsOptionProvided`, and `ExpandAtFileAsync` verbatim. A separate static class (rather than bloating `ProjectCommands`) matches the existing `ProjectRepoCommands` split in `MohistCliCommands.ProjectRepo.cs`.

**Alternative considered:** put everything inline in `ProjectCommands` — rejected because `ProjectCommands.Build` is already the registration hub and the workflow subgroup is large enough (9 verbs) to deserve its own file, consistent with `ProjectRepoCommands`.

### D2. `config set` flags map 1:1 to server verbs, with project-only full-replace via `--vars-file`

`set` is a composite command: only categories whose flags appear are mutated. Flag → endpoint mapping:

| Flag | Method & Path | Notes |
|---|---|---|
| `--default-template <id>` | `PUT /workflow-profile/default-template` | selects an existing template id (distinct from issue-level `--template` body) |
| `--var k=v` / `--stage-var <stage>.k=v` | `PATCH /workflow-profile/variables` | incremental merge; unchanged keys preserved |
| `--vars-file <file>` | `PUT /workflow-profile/variables` | **wholesale full-replace** — unique to the project API (issue-level only merges); reads JSON bundle |
| `--prompt <key>=<body\|@file>` | `PUT /workflow-profile/prompts/{key}` | `@file` reads UTF-8 body |

`--vars-file` is **mutually exclusive** with `--var`/`--stage-var` (validated up-front, exit non-zero with a clear message), because mixing a destructive full-replace with an incremental merge in one invocation is ambiguous. With no flags at all, the command makes no request and exits non-zero ("nothing to change"). `clear` mirrors this composition (`--default-template` → DELETE default-template; `--var k` → variables PATCH with `k:null`; `--prompt <key>` → DELETE prompt).

**Rationale:** direct 1:1 flag→verb mapping keeps the CLI a thin, predictable wrapper over the existing API (no client-side variable merging logic to drift). Surfacing the project-only PUT as an explicit `--vars-file` makes the destructive-replace intent visible on the command line rather than implicit.

**Alternative considered:** auto-detect merge-vs-replace from a single `--vars` flag — rejected because full-replace is destructive and must be opt-in and unambiguous.

### D3. `mo issue session followup` reuses the agent-session followup machinery wholesale

Register a 5th subcommand in the existing `IssueCommands.BuildSession` group (`MohistCliCommands.Issue.cs:854`) via `session.Subcommands.Add(BuildSessionFollowup(api))`. The new verb takes the same two positionals as the sibling verbs (`NumberArg()` + `SessionNameArg()`), the same `--text`/`--text-file`/`--text-stdin` triple resolved by `BodyInputResolver.ResolveAsync`, and `POST`s to `/api/projects/{id}/issues/{num}/sessions/{name}/followup`. It reuses:

- `BodyInputResolver` for text-source resolution + mutual-exclusivity + empty-body validation (identical to `AgentCommands.BuildSessionFollowup`).
- The generic `PrintPostWithOutputAsync(..., rawJson: true)` path for success (`delivery: sent`) **and** error surfacing — no special-casing of `session_inactive`/`runner_offline`. The server returns `409 session_inactive`, `503 runner_offline`, `404` (unknown session); `PrintResponseAsync` already prints `error (code)` to stderr and exits `4` for 404 / `1` otherwise.
- The existing `TableShape.AgentSessionFollowup` renderer (prints `delivery: <status>`) — no new renderer needed.

**Rationale:** the agent-session followup already solved text-source handling and honest error surfacing; the issue-session endpoint returns the identical `{status:"sent"}` success shape and the identical error codes. Reusing the renderer and `BodyInputResolver` avoids ~80 lines of duplicated logic and keeps behaviour consistent across the two followup verbs.

**Alternative considered:** a dedicated `TableShape.IssueSessionFollowup` — rejected as needless divergence; the rendered output (`delivery: sent`) is identical.

### D4. Formalise the already-shipped `mo issue archive --all-completed`

The flag and the `POST /issues/archive-completed` call already exist (`IssueCommands.BuildArchive`, lines 777–781). This change adds: (a) CLI reference documentation under `## Issue 管理`, (b) test coverage via the recording harness, and (c) a `-o table|json` option (currently it uses the raw `PrintPostAsync` which only prints JSON `data`/"OK" — aligning it with the other verbs). Because `--all-completed` currently **silently wins** if both `<number>` and the flag are passed, we add an explicit mutual-exclusivity validation (exit non-zero with a clear message) while we are formalising the verb.

**Rationale:** the wiring exists; the work is documentation + tests + a small UX hardening. Fixing the silent-precedence latent bug now is cheaper than carrying an undocumented footgun.

**Alternative considered:** leave the silent precedence as-is — rejected; an undocumented "flag silently ignores the positional" behaviour is exactly the kind of thing the formalisation step exists to remove.

### D5. Table rendering: reuse where shapes match, add minimal new shapes

Add new `TableShape` values only where the project payload genuinely differs:

- `ProjectTemplateList` / `ProjectTemplateShow` — new renderers (project templates have a different shape than issue templates); place in `TableRenderer.Issues.cs` alongside the existing `RenderIssueTemplateList`, or a new `TableRenderer.ProjectWorkflow.cs` partial if the cluster grows.
- Project `config get` / `set` / `clear` / `preview` — **reuse** `RenderWorkflowProfile` / `RenderWorkflowProfileVariables` / `RenderWorkflowProfilePrompt` / `RenderWorkflowProfilePreview` if the project `/workflow-profile` payload shape matches the issue-level one (needs a quick shape check during implementation; the same `ProjectWorkflowProfileManager` backs both, so the shapes are expected to align). If they diverge, add thin project-specific renderers rather than overloading the issue renderers.

**Rationale:** the `partial class TableRenderer` split is explicitly designed for this (header comment in `TableRenderer.cs` cites "design.md §决策 2"); reuse keeps the render surface small.

### D6. Tests mirror the two closest existing spec files

- `mo project workflow *` → new `CliProjectWorkflowCommandSpecs.cs`, modelled on `CliIssueWorkflowConfigSpecs.cs` (composite set/clear, `@file`, replace-vs-merge mutual exclusivity, template CRUD round-trip, 404 surfacing).
- `mo issue session followup` → extend `CliIssueSessionSpecs.cs`, modelled on the `SessionFollowup_*` cases in `CliAgentSessionCommandSpecs.cs` (success `delivery: sent`, file/stdin sources, `session_inactive` 409, `runner_offline` 503, unknown-session 404, missing-text validation).
- `mo issue archive --all-completed` → extend `CliIssueCommandSpecs.cs` (batch success, JSON output, no-resolvable-project error, mutual-exclusivity with `<number>`).

All tests use the existing `RecordingHttpHandler` + `FakeFileSystem` (pre-seeded with `activeProjectId`) + `FakeCommandExecutor` harness and `MohistCliCommands.RunAsync` entry point.

## Risks / Trade-offs

- **[Project `/workflow-profile` payload shape may differ from issue-level]** → Mitigation: implementation does a quick shape diff against `ProjectWorkflowProfileManager`; reuse `RenderWorkflowProfile` only if it fits, otherwise add a thin project renderer. Either way the spec's "surface default-template id, variables, prompt overrides in one view" is satisfied.
- **[`--vars-file` full-replace is destructive]** → Mitigation: it is an explicit, documented flag, mutually exclusive with incremental flags, and covered by a replace-vs-merge test. The server is the source of truth for the replace.
- **[`archive --all-completed` silent precedence is a latent bug being changed]** → Mitigation: the current behaviour is undocumented and untested, so no user has a relied-upon contract; the new mutual-exclusivity check is strictly safer. Covered by a test.
- **[`session_inactive`/`runner_offline` exit code is `1`, not a dedicated code]** → Mitigation: acceptable — it matches the existing `mo agent session followup` behaviour exactly (only 404 maps to `4`). The spec requires non-zero exit + honest code/message surfacing, both satisfied. Documented as-is.
- **[`mo issue session` (singular) group is itself undocumented in `docs/cli-reference.md` today]** → Mitigation: documenting `followup` is the trigger to document the whole singular group (`show`/`transcript`/`compact`/`reset`/`followup`), which is a net improvement and in scope for "new commands appear in the CLI reference".

## Migration Plan

This is a CLI-only, purely additive change (new subcommands + documentation/tests for an existing flag). There is no data migration, no server contract change, and no breaking change to any existing command.

- **Deploy:** rebuild and ship the `mo` CLI package (`packages/cli/`); no server restart required.
- **Order:** the three command groups are independent and can land in a single PR.
- **Rollback:** revert the CLI package; server and Web UI are untouched. No state was migrated.

## Open Questions

- **Project vs issue workflow-profile payload shape:** confirm at implementation time whether `GET /api/projects/{id}/workflow-profile` returns the same JSON shape as the issue-level profile (so `RenderWorkflowProfile` reuses cleanly). Expected yes (same manager), but verified during D5.
- **`mo issue archive` mutual-exclusivity:** the spec does not explicitly require rejecting `mo issue archive 42 --all-completed`. D4 proposes adding the check as latent-bug hardening; if the integrator prefers strict spec-literalism, the check can be dropped without affecting acceptance criteria (the test would just be removed).
- **`compact` output-mode drift in docs:** `docs/cli-reference.md` mentions a `compact` output mode that `ValidateOutputMode` does not accept — out of scope here, but worth noting for a future doc-cleanup issue.
