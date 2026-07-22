## Context

`mo` is a .NET `System.CommandLine` application. `MohistCliCommands` currently constructs its command tree from many partial command families, each of which directly combines option registration, Project resolution, API calls, formatting, and exit handling. `MohistCliApi` accepts the Server's `{ success, data, error, code, details }` envelope, but it exposes several send-and-print methods and command-specific `--output` renderers. The only current Project fallback is the locally selected `~/.mohist/cli-state.json`; `IFileSystem` already exposes the current directory but Project resolution does not use it.

The change makes the CLI's agent-facing behavior consistent without moving command ownership, state decisions, execution, or persistence out of their existing boundaries. The Server envelope remains the CLI-to-Server transport format and Runner protocols remain unchanged. The requirements are defined by the [proposal](proposal.md) and the three capability specs under [specs](specs/).

Stakeholders are agents and scripts that require bounded, parseable output; interactive operators; and maintainers who need one migration path rather than command-family-specific conventions.

## Goals / Non-Goals

**Goals:**

- Establish one CLI invocation contract for Project selection, local validation, diagnostics, cancellation, and exit classification.
- Make resource output declarative, locally discoverable, and machine-stable while keeping Server response envelopes internal to the CLI.
- Preserve Server, Runner, domain ownership, and existing HTTP routes while making write uncertainty visible instead of retrying it.
- Provide faked CLI seams for filesystem/current-directory state, terminal interactivity, cancellation, HTTP attempt outcomes, and stdout/stderr assertions.

**Non-Goals:**

- Adding a generic query language, JSONPath/JQ selector, template renderer, YAML renderer, or a second agent-only command tree.
- Redesigning domain areas, actions, routes, DTO ownership, persisted Project state, or Runner dispatch.
- Reducing Server response payload size in this change; field selection controls CLI output, not Server-side projections.
- Retaining `--project-id`, `--output`, or the former boolean JSON-output semantics as compatibility aliases.

## Decisions

### D1: Introduce a shared CLI contract layer at the command boundary

Add a small set of CLI-owned types behind command registration:

- `CliInvocation`: stdout, stderr, stdin, interactivity state, environment, cancellation token, and the normalized exit result.
- `ProjectReferenceResolver`: produces `Resolved`, `Missing`, or `InvalidContext` before a domain handler runs.
- `CliResponseReader`: performs a request once, unwraps the Server envelope, and returns either a success `JsonNode` or a structured `CliFailure`.
- `CliResultWriter`: owns human result rendering, JSON/NDJSON writing, diagnostics, and the four exit-code classes.

Command handlers keep their domain path/body construction but call these shared services instead of writing to streams, resolving Project flags, parsing envelopes, or mapping transport errors themselves. The top-level runner owns parse-error and cancellation mapping so `System.CommandLine` errors become `2`, an operation failure becomes `1`, and root cancellation becomes `130`. The existing stream-specific cancellation handler is migrated to the same root cancellation source.

This puts formatting and failure policy in one authority while keeping command-specific request intent local. It also allows `RunAsync` tests to inject terminal state and cancellation without using the process console.

Alternative considered: add a generic middleware pipeline around every `System.CommandLine` action. This would centralize parsing, but it cannot know a command's Project scope, resource cardinality, fields, or mutation semantics without a second metadata channel. A narrow contract layer plus descriptors makes those facts explicit and testable.

### D2: Resolve Project references locally through ordered context sources

Replace the tuple returned by `ProjectRefOption()` with one `--project` option and a `ProjectReferenceResolver`. It checks, in order: the explicit option; the nearest current-directory Project context; then the existing locally selected Project state. The resolver returns a tagged result rather than writing errors itself, allowing the common writer to produce the one required actionable diagnostic.

The current-directory reader uses `IFileSystem.CurrentDirectory` and walks toward the filesystem root for the nearest `.mohist/cli-state.json` containing exactly one non-blank `activeProjectId`; the user-home state at `~/.mohist/cli-state.json` remains the final selected-Project fallback. `mo project use` is the sole writer for this CLI-owned context: after the Server resolves its argument to a canonical id, it writes the same small record to both the current directory and user-home locations. An invalid nearest record fails locally rather than falling through silently. Project names are globally unique in the Server model, so an explicit name is passed to the existing endpoint resolver and cannot be locally ambiguous.

Alternative considered: resolve names by listing Projects from the Server before every command. This violates offline local validation and introduces a request before commands that only need help or field discovery. The explicit reference remains opaque until the existing endpoint resolves it; only local source selection is performed before a domain request.

### D3: Declare resource output once per leaf command and project locally

Each resource-returning leaf command registers a `ResourceDescriptor` with:

- result cardinality: `Single`, `Collection`, or `Stream`;
- exact top-level field names accepted by `--json`;
- the existing human renderer, if one exists;
- whether the command performs a read or mutation.

`JsonSelection.Parse` has three states: no JSON selection, field discovery, and a validated ordered field list. Bare `--json` reads only the descriptor and writes one JSON array of its field-name strings in descriptor order. A selected list projects the decoded `data` node into new `JsonObject` values, preserving selected field order and excluding all other fields. For collections it projects each element into an array; streams continue to pass one validated JSON object per line and never enter the envelope/table pipeline. Dotted paths, arbitrary expressions, and automatic DTO/reflection discovery are intentionally excluded: field names are an explicit public contract owned next to the command that exposes them.

`--output` registration and the existing `ResolveOutputMode` path are removed from these resource leaves. Without `--json <fields>`, a command uses its descriptor's human renderer; commands that have no human renderer use their existing textual result path. All result writers receive stdout and all progress/diagnostic writers receive stderr from `CliInvocation`.

Alternative considered: add a Server `fields` query parameter and project at each API route. That would reduce payload transfer but multiplies API behavior across routes, requires DTO-specific authorization/projection rules, and exceeds the proposal's no-Server-protocol scope. Local projection makes the CLI contract uniform first.

### D4: Normalize transport responses and failures before rendering

`CliResponseReader` is the only HTTP boundary for migrated command handlers. It parses the existing success envelope once, keeps `data` private until result rendering, and maps a failure into `CliFailure { Code, Message, Details, AttemptState }`. A non-blank Server `code` is preserved. Any HTTP failure without one, including a normal error envelope, receives the stable fallback `http-<status>`; transport failures receive a stable transport code such as `server-unavailable`. The diagnostic formatter preserves the envelope message and details, extracts affected object, current state, and rejection reason from known detail members when present, then emits one hint only from an explicit `CliHintResolver` mapping.

The hint resolver is a closed mapping from stable codes and structured details to command lines. It returns no hint by default. This prevents speculative advice and makes the "one hint" rule mechanically enforceable.

For mutating verbs, request execution records `NotSubmitted`, `OutcomeUnknown`, or `Completed`. Failures before an HTTP send is attempted are `NotSubmitted`; any transport exception after send begins is conservatively `OutcomeUnknown`, unless the transport adapter can prove that no request reached the Server. `OutcomeUnknown` produces an explicit diagnostic and never retries. Read-only commands may retain their existing no-retry behavior; this change introduces no automatic retry policy.

Alternative considered: infer delivery from the exception text or retry every idempotent-looking request. Exception text is not a transport proof, and CLI-side assumptions about idempotency can duplicate state transitions. The conservative classification exposes uncertainty to the caller instead.

### D5: Migrate through descriptors and contract guards, not a wholesale command rewrite

Keep the existing command tree and domain handlers, then migrate leaf commands family by family. A structural test enumerates Project-scoped leaves and resource descriptors, asserting that the former dual Project options and `--output` registrations are absent and that every resource leaf has exactly one descriptor. Behavior specs use `RecordingHttpHandler`, fake filesystem/current directory, injected terminal state, and fixed cancellation signals; no test contacts a Server, Runner, process, or real terminal.

Alternative considered: replace the command tree with a generated DSL or reflective DTO command system. It would obscure existing domain command behavior and make the initial migration larger. Explicit registrations preserve local command vocabulary and allow incremental review.

## Risks / Trade-offs

- [Client-side projection still transfers full Server payloads] -> Keep field descriptors and projection in the CLI for this change; measure payload pressure before proposing an API-level `fields` protocol.
- [Migrating many command partials can leave one legacy path behind] -> Add command-tree structural guards and migrate by command family with stdout, stderr, exit-code, and no-request-on-local-error specs.
- [Current-directory context can select the wrong workspace if markers are stale] -> `mo project use` writes the marker, resolution uses the nearest marker only, explicit `--project` wins, and malformed markers fail locally rather than falling through silently.
- [Server failures are not uniformly detailed today] -> Preserve existing non-blank `code` and `details`; synthesize `http-<status>` for every code-less HTTP failure and a stable transport code for every transport failure; add structured details only where a command needs them to satisfy its diagnostic contract.
- [A write may succeed after the client loses the response] -> Treat every post-send transport exception as outcome unknown, return exit `1`, and never retry automatically.
- [Redirecting legacy progress text can affect human scripts] -> This is an intentional contract change; stage command-family migration with documented stdout/stderr assertions and retain no compatibility alias.

## Migration Plan

1. Add `CliInvocation`, `ProjectReferenceResolver`, `CliResponseReader`, `CliResultWriter`, `CliHintResolver`, terminal-state abstraction, and transport-attempt abstraction with unit coverage for all outcome classes.
2. Add `ResourceDescriptor` and `JsonSelection`; migrate representative single, collection, stream, and mutation commands first to lock field discovery, projection, NDJSON, and stdout/stderr behavior.
3. Migrate every Project-scoped and resource-returning leaf command family. Make `mo project use` write both user-home and current-directory records; remove `ProjectIdOption`, dual-option reads, output-mode registration, raw envelope rendering, and local send-and-print duplicates as each family moves.
4. Route all parse failures and cancellation through the root contract. Move prompt text to stderr and gate every prompt through terminal interactivity and `MOHIST_PROMPT_DISABLED` before any stdin read or state modification.
5. Add structural and behavior tests, run the CLI test suite, and release the breaking CLI surface in one version. Update bundled skill guidance and user-facing command references in the same release.

Rollback is a package-version rollback. No database migration, persisted-model migration, Server route migration, or Runner deployment coordination is required. A code rollback restores the former CLI behavior; it must not reintroduce compatibility aliases into the new release line.

## Open Questions

None. Each resource descriptor's field list is chosen with the command migration from its existing response DTO and command purpose; it does not require a shared resource schema.
