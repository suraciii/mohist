## Context

Issue 481 separates three facts that the CLI currently places under the plural `events` command: a persistent Activity read, a transient realtime Event subscription, and dead-letter recovery. The current CLI registers `EventCommands.Build` at the root (`packages/cli/Mohist.Cli/MohistCliCommands.cs`) and builds the plural `events` group with `tail` and `dead-letter list/redeliver` (`MohistCliCommands.Event.cs`). There is no `activity` CLI group.

The server already has the three underlying concerns, but their contracts differ:

- `GET /api/projects/{projectRef}/agent/activity?limit=` assembles the Web Activity feed. Its `ActivityDto` is a composite object containing `summary`, session activity cards, and waiting cards; `AgentActivityFeedAssembler` bounds its session-card query to 1 through 200.
- `GET /api/projects/{projectRef}/events/tail` opens a transient, project-scoped `IEventTailSource` subscription, validates `match` on the server, and writes envelope-only NDJSON. It deliberately does not replay envelopes from before subscription establishment.
- `GET/POST /api/events/dead-letters` are global operator routes. They are registered only for loopback-only server listeners and require `OperatorCredential`; the CLI independently refuses to read or send that credential to a non-loopback base URL.

Issue 475 supplies the CLI-wide ProjectRef and `--json` contracts. `PrintResourceAsync` already applies a `ResourceDescriptor` to a normal API envelope and can render either selected JSON or a human view. It is the right path for `activity list`; the tail keeps its separate NDJSON reader, and dead-letter commands keep their table/output-mode flow.

The Web continues to consume the composite `/agent/activity` feed on a five-second refresh. The CLI needs a collection, not a second Activity aggregate or a breaking change to that Web contract.

## Goals / Non-Goals

**Goals:**
- Expose `mo activity list` as a project-scoped, finite, persistent Activity read with `--limit`, `--project`, and field-selection output.
- Move tail and dead-letter operations to the singular `mo event` noun without changing their server delivery, matching, cancellation, or operator-credential semantics.
- Make the command tree, leaf help, hints, examples, and regression tests express the distinct meanings of persistent history, realtime observation, and recovery side effects.
- Preserve routing rule CRUD and `routing test` as the sole routing-management and match-evaluation surface.

**Non-Goals:**
- Do not change the content, ordering, lifecycle semantics, or persistence of the Activity aggregate; the CLI list is a collection projection of existing Activity session cards.
- Do not alter CloudEvent schemas, Event tail replay semantics, the server-side match language, routing rules, or delivery retry policy.
- Do not add historical Event replay, arbitrary time-range scans, cross-project tails, logs, traces, metrics, or a generic `event list` command.
- Do not retain plural `events` as an alias or compatibility path.

## Decisions

### D1 - Add an Activity collection endpoint that projects the existing feed

Add `GET /api/projects/{projectRef}/activity?limit=` as a project-resolved read endpoint. It will call `AgentActivityFeedAssembler.GetActivityAsync(project.Id, limit, ct: ct)` and return the resulting `Sessions` collection in the normal API envelope. It will not return the Web feed's `summary` or `waiting` companions and will not alter `/agent/activity`, `ActivityDto`, `ActivityCardDto`, or the assembler's source queries.

The endpoint validates `limit` in the same 1 through 200 range already used by the assembler; the CLI also validates that range before sending a request. The default is 50, matching the assembler's default bounded read. This gives the CLI a finite, project-scoped collection whose JSON form can be field-projected without making callers understand a Web-specific composite object.

**Alternatives considered:**

- Have the CLI fetch `/agent/activity` and extract `sessions` locally. Rejected because the CLI's generic JSON selection expects a collection at the API boundary, and client-side envelope extraction would create a special transport path that leaks the Web response shape into the CLI.
- Change `/agent/activity` to return an array. Rejected because the Web relies on `summary` and `waiting`; changing the shared API would be a breaking change unrelated to the CLI command migration.
- Create a new Activity persistence model or synthesize a cross-domain event ledger. Rejected because it changes the aggregate content and persistence semantics expressly excluded by this issue.

### D2 - Implement activity list with the shared resource-output path

Introduce an `ActivityCommands` root group with a single `list` leaf, registered next to the other root resource groups. The leaf declares `ProjectRefOption`, hidden legacy `--project-id`, `--limit` (default 50, valid 1 through 200), and `JsonSelectionOption`.

Define one `ResourceDescriptor` with `ResourceCardinality.Collection` and the stable top-level fields of `ActivityCardDto`: `issueNumber`, `issueTitle`, `issueStage`, `issueRuntimeStatus`, `sessionId`, `status`, `model`, `title`, `createdAt`, `completedAt`, `lastActivityAt`, `currentWorkItem`, `taskProgress`, `lastActivity`, `failureReason`, `agentId`, `agentName`, `eventSummary`, and `usage`. Parse field selection before project resolution or HTTP work; bare `--json` discovers the declared fields and invalid field lists fail locally with exit code 2.

After resolving the project through the issue-475 resolver, call `PrintResourceAsync` for `/api/projects/{resolved}/activity?limit={limit}`. Add `ActivityList` to `MohistCliApi.TableShape` and a renderer that presents the scan-oriented card fields: issue number/title/stage, session id, status, model, and last activity time. Selected JSON stays a flat array of the declared card fields; it does not wrap list metadata or emit the Web feed object.

**Alternatives considered:**

- Reuse `AgentSessionList` and its renderer. Rejected because Activity cards carry Issue/workflow context and last-activity projections that a session list intentionally omits; calling them the same resource would make the Activity command lose its user-visible meaning.
- Use `PrintWithOutputAsync` and add Activity to the global output catalog. Rejected because `PrintResourceAsync` accepts the command-owned descriptor directly and avoids turning the Activity card field contract into a table-shape inference rule.
- Stream Activity through the Event tail code. Rejected because Activity is a finite persisted read and must remain re-readable after command exit.

### D3 - Rename only the CLI command noun from events to event

Keep `EventCommands`, `BuildTail`, `RunTailAsync`, dead-letter request paths, `OperatorCredentialProvider`, `EventTailSource`, and the server routes unchanged. Change only the command instance created in `EventCommands.Build` from `events` to `event`, retaining `tail` and `dead-letter` as its subcommands. The API remains plural in `/events/tail` and `/events/dead-letters` because those paths describe delivery infrastructure, not the user-facing CLI language.

The tail continues to use `ResourceCardinality.Stream` and `NdjsonStream.ReadAsync`/`ReadSelectedAsync`; an explicit `--match` is URI-encoded and compiled only by `ProjectEventTailRoutes`. Ctrl-C keeps cancelling the stream and returning 130. Dead-letter list and redeliver keep their existing 1 through 500 limit, handler encoding, positive-id validation, loopback preflight, credential lookup, request header, error rendering, and terminal-text sanitization.

**Alternatives considered:**

- Rename the server `/events/*` routes to singular as well. Rejected because it changes stable server APIs without delivering the requested user-facing separation and would require an unnecessary compatibility migration.
- Retain `events` as a hidden alias. Rejected because the requirements explicitly remove the plural navigation and aliases would keep ambiguous hints and scripts alive.
- Add `event list` as an alias for Activity. Rejected because it reintroduces the exact semantic confusion this issue removes.

### D4 - Make the separation executable through help, docs, and focused tests

Root help will show `activity` and `event`, never `events`. Leaf descriptions will be explicit: `activity list` describes a bounded persistent history; `event tail` names its post-subscription live-envelope origin and NDJSON; `event dead-letter` names inspection plus a retry side effect. No command receives a mode/source switch.

Update the command-surface tests rather than only changing command invocations:

- Add CLI specs for Activity list covering route/query encoding, `--project` override, no-active-project failure before HTTP, 1 through 200 local limit validation, bare/selected/invalid `--json`, human table rendering, and re-reading an unchanged response.
- Rename/update tail and dead-letter specs to invoke singular `event`; add explicit plural-path rejection assertions while retaining their existing streaming, match-diagnostic, cancellation, credential, and sanitization coverage.
- Replace the root-shape assertion that currently rejects singular `event` with assertions that singular `event` and `activity` appear and plural `events` does not resolve or appear in help. Assert `event list` does not resolve.
- Add a server API spec for the new collection projection, including project isolation, default/valid limits, invalid-limit rejection, and equivalence to the existing Activity feed's session cards. Keep existing `/agent/activity` specs unchanged as regression coverage for the Web feed.

Update `docs/cli-reference.md` command maps and its implementation-gap note. Do not change unrelated `issue events` terminology or routing response payload fields named `events`; those are different resource surfaces, not the removed root command.

**Alternatives considered:**

- Rely on existing Event tests after changing the command constructor. Rejected because they would not guard removal of the plural form, absence of `event list`, help semantics, or the new Activity collection boundary.
- Broadly replace every textual `events` occurrence. Rejected because Issue event reads and routing payload property names are unrelated and such a replacement would cause regressions.

## Risks / Trade-offs

- **[The new Activity route projects only session cards, while the Web feed also contains summary and waiting cards]** -> Mitigation: document it as a CLI collection projection, preserve the Web composite endpoint unchanged, and do not claim a new aggregate or event ledger.
- **[A second public Activity route could drift from `/agent/activity`]** -> Mitigation: make it delegate to `AgentActivityFeedAssembler` and add an API equivalence spec; do not duplicate session, issue-title, transcript, or task-progress assembly.
- **[Limit behavior could diverge between CLI and server]** -> Mitigation: define one 1 through 200 range, validate locally for actionable no-request failures, and validate again on the server boundary.
- **[Plural command removal breaks scripts]** -> Mitigation: this is an intentional breaking change. Return the normal unknown-command failure without a compatibility alias, and update all repository help, hints, examples, and CLI tests in the same change.
- **[Dead-letter credential protections weaken during noun migration]** -> Mitigation: move the existing handlers unchanged under `event`; preserve tests proving non-loopback refusal occurs before credential lookup/request and missing credentials fail before HTTP.
- **[Activity list looks like an event replay]** -> Mitigation: use distinct command/help wording, a bounded normal response rather than NDJSON, and no `event list` path.

## Migration Plan

1. Add the server Activity collection projection and server specs. Deploying it alone is additive and does not change Web clients using `/agent/activity`.
2. Add `ActivityCommands`, its descriptor/table renderer, and CLI specs; register the singular `activity` group.
3. Change the event root command noun to singular, update all affected CLI invocations and root-shape assertions, then remove every plural `events` hint/example reference.
4. Update the CLI reference command map and close its recorded implementation gap. Run `npm test` for server and the CLI test project through the repository's normal .NET test command.

The route addition is additive, but the CLI noun migration is intentionally breaking. Roll back before release by restoring the prior CLI command tree and documentation; the added Activity endpoint can remain harmlessly deployed because it is read-only and has no persistence or schema migration. After release, rollback cannot preserve both the no-alias requirement and old `events` scripts, so a rollback restores the old behavior wholesale rather than adding a compatibility alias.

## Open Questions

None. The Activity list contract is deliberately limited to the existing session-card collection; expanding Activity aggregate content or exposing Web-only summary/waiting companions belongs to a separate change.
