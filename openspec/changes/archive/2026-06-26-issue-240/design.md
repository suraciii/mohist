## Context

The server already serves 10 endpoints under `/api/projects/{p}/issues/{n}/workflow-profile/*` (template get/put/delete, variables get/put/patch, prompts get/put/delete, prompt preview). None are reachable from the CLI. The existing `mo issue workflow` parent command (in `MohistCliCommands.Issue.cs`, `BuildWorkflow`) currently hosts only the runtime subcommands `status` and `timeline`. This change adds a sibling `config` subcommand group that wires the existing profile endpoints — pure CLI client work, no domain model or endpoint changes.

The CLI layer is a thin client: every command parses args, resolves the project, calls `MohistCliApi` HTTP helpers, and renders the envelope. `MohistCliApi` already exposes the verb helpers needed (`PrintWithOutputAsync` GET, `PrintPostWithOutputAsync`, `PrintPatchWithOutputAsync`, `PrintDeleteWithOutputAsync`, `PrintPutAsync`, `PrintDeleteAsync`). File reading goes through the injectable `IFileSystem` (testable via `FakeFileSystem`). Tests use a `RecordingHttpHandler` harness that asserts on emitted requests.

Constraints:
- Runtime vs configuration state must stay separated — `config` is a sibling of `status`/`timeline`, never merged.
- The variable-clear path may need a one-line server tweak (see Open Questions).

Reference: motivation in `proposal.md`, normative behavior in `specs/cli-interface/spec.md` and `specs/issue-workflow-profile/spec.md`.

## Goals / Non-Goals

**Goals:**
- Expose template / variables / prompts overrides from the CLI via `mo issue workflow config {get,set,clear,preview}`.
- One composite `set` / `clear` invocation can touch multiple config categories, matching server PUT/PATCH semantics.
- `@file` body reading for `--template` and `--prompt`; `k=v` / `stage.k=v` parsing for variables.
- Faithful error passthrough (server errors print and exit non-zero).
- Full `-o table|json` and `--project`/`--project-id` support across all four verbs.
- Reuse existing `MohistCliApi` HTTP helpers and the `RecordingHttpHandler` test harness.

**Non-Goals:**
- No new server endpoints, no domain-model changes.
- No interactive `config edit` (deferred to a later issue).
- No changes to `mo issue workflow status`/`timeline`, `mo issue rebase`, attachment/metrics CLIs.
- No `--model`/`--stage-models` here (those live on `mo issue update`, #161).
- No batching/transaction guarantee across the multiple requests a composite `set` issues — each is independent; partial-failure is reported per-request.

## Decisions

### D1. `config` as a sibling subcommand group under `mo issue workflow`

Add a `Command("config", ...)` with children `get`/`set`/`clear`/`preview`, attached in `BuildWorkflow` next to `statusCmd`/`timelineCmd`. Each child takes the existing shared `number` argument, `ProjectRefOption()`, and `OutputOption()` — identical ergonomics to the runtime verbs.

*Alternative considered:* flatten to `mo issue workflow-config` at the issue root. Rejected — keeps the `workflow` parent cohesive (all workflow concerns one place) and matches the issue's stated shape.

### D2. Composite `set` / `clear` driven by flags, not nested subcommands

`set` accepts repeatable `--template`, `--var`, `--stage-var`, `--prompt`; `clear` accepts `--template`, `--var`, `--prompt`. A flag's presence gates whether its category's request fires; absent flags touch nothing. This matches the server's per-category PUT/PATCH/DELETE semantics and lets one call atomically (from the user's perspective) change several things.

*Alternative considered:* `config template set` / `config vars set` / `config prompts set` nested verbs. Rejected — more typing, more layers, and loses the "change everything in one shot" affordance that aligns with the server's independent endpoints.

### D3. Request mapping per flag (locked to existing endpoints)

| Flag | Method & path (under `/api/projects/:pid/issues/:n`) |
|---|---|
| `get` | `GET  /workflow-profile` |
| `set --template` | `PUT  /workflow-profile/template` |
| `clear --template` | `DELETE /workflow-profile/template` |
| `set --var` / `--stage-var` | `PATCH /workflow-profile/variables` (single merged body) |
| `set --prompt k=...` | `PUT  /workflow-profile/prompts/{k}` (one per occurrence) |
| `clear --prompt k` | `DELETE /workflow-profile/prompts/{k}` |
| `clear --var k` | `PATCH /workflow-profile/variables` with `{ "k": null }` |
| `preview k` | `POST /workflow-profile/prompts/{k}/preview` |

`--var` and `--stage-var` are merged into one variables PATCH body: top-level keys for `--var`, and `{ "<stage>": { "<k>": <v> } }` nesting for `--stage-var`. The key is URL-escaped for the prompt path (`Uri.EscapeDataString`), consistent with existing `Escape` usage.

### D4. `@file` body reading via the injectable `IFileSystem`

For `--template @wf.yaml` and `--prompt k=@file`: if the value starts with `@`, strip the prefix and `ReadAllTextAsync` the remainder through `api.FileSystem`. This reuses the same testable filesystem abstraction as `--body-file` (`BodyInputResolver`), without coupling to `BodyInputResolver` itself (whose error messages are issue-body-specific). A tiny local helper (e.g. `ExpandAtFile(string value, IFileSystem)`) keeps it DRY. Values without `@` are inline bodies.

*Alternative considered:* reuse `BodyInputResolver`. Rejected — its "issue body is required" / mutually-exclusive-source semantics don't fit optional, repeatable prompt flags.

### D5. `k=v` / `stage.k=v` parsing

Split each value on the first `=` only (`Split('=', 2)`), so values may contain `=`. For `--stage-var plan.baz=qux`, split the key on the first `.` to get stage=`plan`, key=`baz`. Empty value side is allowed (sets empty string). Malformed input (no `=`, or `--stage-var` with no `.`) prints a clear error and exits non-zero before any request fires.

### D6. Output rendering — add `PrintPutWithOutputAsync`; reuse table shapes

`get` and `preview` need `-o table`. The profile response is a 3-section object (template/variables/prompts); add a `WorkflowProfile` entry to the `TableShape` enum and a renderer in `TableRenderer.IssueTemplates.cs`. `PrintWithOutputAsync` covers GET; `PrintPostWithOutputAsync` covers preview POST. The template PUT currently has no `*WithOutputAsync` variant — add `PrintPutWithOutputAsync` mirroring the existing Post/Patch variants so `set --template` honors `-o`. Prompt PUTs and all DELETEs reuse `PrintPutAsync`/`PrintDeleteAsync`/`PrintDeleteWithOutputAsync` as appropriate (these return the updated resource envelope).

### D7. No-op guard

`set`/`clear` invoked with zero applicable flags make no HTTP request, print "nothing to change"/"nothing to clear", and exit non-zero. This prevents accidental empty mutations and matches the spec.

## Risks / Trade-offs

- **[Composite `set` issues multiple independent requests, not a server transaction]** → Accept. Each endpoint is independent by design; document that a mid-sequence server failure leaves a partial state and surface each request's status. Do not attempt client-side rollback (would require inverse semantics the server doesn't expose).
- **[Server `PATCH /variables` may store `null` instead of removing the key]** → Mitigate: verify server behavior first; if it persists null, make the one-line server tweak to treat null as removal (per `issue-workflow-profile` spec). Fallback if tweak is undesirable: CLI `clear --var` could `GET` then `PUT` the full variables map minus the key — heavier but server-agnostic. Prefer the null-clear path.
- **[Prompt keys with path-unsafe characters]** → Mitigate via `Uri.EscapeDataString` on the key segment; reject keys containing `/` with a clear error rather than risking path traversal to another prompt.
- **[Large prompt bodies inline on the shell]** → Mitigate via `@file` support; recommend file form for long prompts in `--help` text.
- **[Variable value type ambiguity]** → `k=v` is always a string. If numeric/structured values are needed later, that's a follow-up; the issue explicitly avoids YAML-in-shell nesting.

## Migration Plan

CLI-only feature, no data migration.

1. Implement `config` group + helpers behind the existing command parser; no flags change for existing commands.
2. Add `PrintPutWithOutputAsync` and the `WorkflowProfile` table shape; existing table shapes unaffected.
3. If the server null-clear tweak is needed: extend the `PATCH /variables` handler to drop keys whose value is JSON null, with a server unit test. Add only this one behavior — no existing client sends null today, so backward-compatible.
4. Verify: `mo issue workflow config --help` lists all four verbs; run CLI integration specs; smoke-test against a live server (`config get` round-trip after `config set`).

**Rollback:** revert the CLI commit; the optional server null-clear tweak is independently revertible and affects no current client (no one sends null today).

## Open Questions

- **~~Does the server's `PATCH /workflow-profile/variables` already remove keys on null, or persist null?~~** — RESOLVED during planning: `VariableBundle.DeepMerge`/`MergeNode` (`packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs:163,174`) `continue` past null-valued overlay properties, so `{ "foo": null }` is a no-op today and `foo` persists. The null-clear tweak IS required and is scoped as task T-001 (modify the merge to treat overlay null as key removal). The spec assumption (null = removal) is therefore made true by T-001 rather than already holding.
- **Should `set` with only `--var`/`--stage-var` use PATCH (merge) or PUT (replace) for variables?** The issue's API table lists an optional PUT "覆盖语义，按需选". v1 decision: PATCH (merge) — non-destructive and matches `--var`'s additive intent. PUT-replace is deferred (not exposed in v1); can be added via a `--replace-vars` flag in a follow-up if needed. This is a deliberate scope decision, not an unresolved blocker.
