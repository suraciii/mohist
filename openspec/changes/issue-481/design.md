## Context

Issue 481 separates three facts that the CLI currently places under the plural `events` command: a persistent Activity read, a transient realtime Event subscription, and dead-letter recovery. The current CLI registers `EventCommands.Build` at the root (`packages/cli/Mohist.Cli/MohistCliCommands.cs`) and builds the plural `events` group with `tail` and `dead-letter list/redeliver` (`MohistCliCommands.Event.cs`). There is no `activity` CLI group.

The server already has the three underlying concerns, but their contracts differ:

- `GET /api/projects/{projectRef}/events?limit=` is the persisted Activity evidence feed. `ProjectEventFeedAssembler` merges Issue, WorkflowRun, and AgentSession event stores, orders entries by recorded time and stable identity, and returns a bounded project-scoped collection. The Web combines that feed with its existing AgentOps session/waiting cards and Runner status snapshots to render its Activity page.
- `GET /api/projects/{projectRef}/agent/activity?limit=` supplies the current AgentOps session and waiting snapshots; `RunnerStatusService.GetRunnersAsync(projectId)` supplies the current Runner snapshots. These are durable/current state facts, not replayed Event envelopes.
- `GET /api/projects/{projectRef}/events/tail` opens a transient, project-scoped `IEventTailSource` subscription, validates `match` on the server, and writes envelope-only NDJSON. It deliberately does not replay envelopes from before subscription establishment.
- `GET/POST /api/events/dead-letters` are global operator routes. They are registered only for loopback-only server listeners and require `OperatorCredential`; the CLI independently refuses to read or send that credential to a non-loopback base URL.

Issue 475 supplies the CLI-wide ProjectRef and `--json` contracts. `PrintResourceAsync` already applies a `ResourceDescriptor` to a normal API envelope and can render either selected JSON or a human view. It is the right path for `activity list`; the tail keeps its separate NDJSON reader, and dead-letter commands keep their table/output-mode flow.

The Web continues to consume these sources separately. The CLI needs one finite collection, but it must preserve the evidence distinction rather than narrow Activity to AgentSession cards or expose a second persistence model.

## Goals / Non-Goals

**Goals:**
- Expose `mo activity list` as a project-scoped, finite Activity evidence read with `--limit`, `--project`, and field-selection output. It includes persisted Issue/WorkflowRun/AgentSession facts and the existing current AgentOps, waiting, and Runner facts, with explicit provenance.
- Move tail and dead-letter operations to the singular `mo event` noun without changing their server delivery, matching, cancellation, or operator-credential semantics.
- Make the command tree, leaf help, hints, examples, and regression tests express the distinct meanings of persistent history, realtime observation, and recovery side effects.
- Preserve routing rule CRUD and `routing test` as the sole routing-management and match-evaluation surface.

**Non-Goals:**
- Do not change the content, ordering, lifecycle semantics, or persistence of the existing Activity evidence sources; the CLI list is a bounded projection of their existing recorded events and snapshots.
- Do not alter CloudEvent schemas, Event tail replay semantics, the server-side match language, routing rules, or delivery retry policy.
- Do not add historical Event replay, arbitrary time-range scans, cross-project tails, logs, traces, metrics, or a generic `event list` command.
- Do not retain plural `events` as an alias or compatibility path.

## Decisions

### D1 - Add one Activity evidence projection over the existing recorded feed and snapshots

Add `GET /api/projects/{projectRef}/activity?limit=` as a project-resolved read endpoint backed by an `ActivityEvidenceAssembler`. It reads the persisted `ProjectEventFeedAssembler` collection for Issue, WorkflowRun, and AgentSession history, then reads the same AgentOps session/waiting and Runner status snapshots already used by the Activity page. It maps all inputs into one `ActivityEntryDto` collection without writing data, changing event emission, or changing `/events`, `/agent/activity`, or Runner APIs.

Extract the existing route-private waiting-card projection from `AgentRoutes` into a shared AgentOps read helper so `/agent/activity` and the new assembler consume the same waiting facts. The new assembler obtains Runner capacity/status through `RunnerStatusService` and passes the shared waiting/capacity inputs to `AgentActivityFeedAssembler`; it does not reimplement session, issue-title, transcript, task-progress, or runner-status derivation.

`ActivityEntryDto` has a stable `id`, `provenance` (`recorded` or `snapshot`), `kind` (`issue`, `workflow-run`, `agent-session`, `runner`, or `waiting`), `time`, human-readable `title` and `description`, and nullable source identity fields (`eventType`, `issueNumber`, `workflowRunId`, `sessionId`, `runnerId`, `status`). Recorded entries preserve the Project Event identity/type/time; snapshots use deterministic identities derived from their existing resource identity. The endpoint merges all entries with a stable time-descending sort and applies the requested limit to the final collection.

Use one declared range of 1 through 200 with a default of 100. The CLI validates it before HTTP and the route validates it again. A repeated read never consumes recorded history; snapshot rows change only when their existing source state changes.

**Alternatives considered:**

- Have the CLI fetch only `/agent/activity` and extract `sessions` locally. Rejected because it loses the persisted Issue/WorkflowRun/AgentSession evidence and Runner snapshots, and leaks the Web response shape into the CLI.
- Make `activity list` a CLI alias for `/events`. Rejected because the recorded feed alone omits the existing AgentOps, waiting, and Runner snapshots that form the current Activity aggregate.
- Create a new Activity persistence model or synthesize a new cross-domain event ledger. Rejected because existing Project Event persistence plus existing snapshots already provide the required facts; new storage changes the aggregate content and persistence semantics excluded by this issue.

### D2 - Implement activity list with the shared resource-output path

Introduce an `ActivityCommands` root group with a single `list` leaf, registered next to the other root resource groups. The leaf declares `ProjectRefOption`, hidden legacy `--project-id`, `--limit` (default 100, valid 1 through 200), and `JsonSelectionOption`.

Define one `ResourceDescriptor` with `ResourceCardinality.Collection` and the `ActivityEntryDto` fields: `id`, `provenance`, `kind`, `time`, `title`, `description`, `eventType`, `issueNumber`, `workflowRunId`, `sessionId`, `runnerId`, and `status`. Parse field selection before project resolution or HTTP work; bare `--json` discovers the declared fields and invalid field lists fail locally with exit code 2.

After resolving the project through the issue-475 resolver, call `PrintResourceAsync` for `/api/projects/{resolved}/activity?limit={limit}`. Add `ActivityList` to `MohistCliApi.TableShape` and a renderer that presents provenance, kind, time, title, and the most relevant source identity. Selected JSON stays a flat array of declared Activity evidence fields; it does not wrap list metadata or emit raw source response objects.

**Alternatives considered:**

- Reuse `AgentSessionList` and its renderer. Rejected because it drops the persisted Issue/WorkflowRun evidence and Runner snapshots, making Activity a mislabeled session list.
- Use `PrintWithOutputAsync` and add Activity to the global output catalog. Rejected because `PrintResourceAsync` accepts the command-owned descriptor directly and avoids turning the Activity card field contract into a table-shape inference rule.
- Stream Activity through the Event tail code. Rejected because Activity is a finite evidence read whose recorded facts and snapshots must remain readable after command exit.

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

- Add server specs for the Activity evidence assembler and route: persisted Issue/WorkflowRun/AgentSession entries, Runner and waiting/session snapshots, recorded-versus-snapshot provenance, project isolation, stable ordering, final-limit enforcement, and unchanged-source re-reads. Add CLI specs for route/query encoding, `--project` override, no-active-project failure before HTTP, 1 through 200 local limit validation, bare/selected/invalid `--json`, human table rendering, and recorded/snapshot field selection.
- Rename/update tail and dead-letter specs to invoke singular `event`; add explicit plural-path rejection assertions while retaining their existing streaming, match-diagnostic, cancellation, credential, and sanitization coverage.
- Replace the root-shape assertion that currently rejects singular `event` with assertions that singular `event` and `activity` appear and plural `events` does not resolve or appear in help. Assert `event list` does not resolve.
- Keep existing `/events`, `/agent/activity`, and Runner-status specs unchanged as regression coverage for the source read models; the new Activity route proves its projection rather than redefining any source contract.

Update `docs/cli-reference.md` command maps and its implementation-gap note. Do not change unrelated `issue events` terminology or routing response payload fields named `events`; those are different resource surfaces, not the removed root command.

**Alternatives considered:**

- Rely on existing Event tests after changing the command constructor. Rejected because they would not guard removal of the plural form, absence of `event list`, help semantics, or the new Activity collection boundary.
- Broadly replace every textual `events` occurrence. Rejected because Issue event reads and routing payload property names are unrelated and such a replacement would cause regressions.

## Risks / Trade-offs

- **[The Activity route combines recorded history and current snapshots]** -> Mitigation: make `provenance` mandatory, preserve recorded event identity/type/time, give snapshots deterministic identities, and state the distinction in help and field discovery.
- **[A second public Activity route could drift from its existing sources]** -> Mitigation: delegate to `ProjectEventFeedAssembler`, `AgentActivityFeedAssembler`, and `RunnerStatusService`; add projection tests for every source kind rather than duplicating persistence or lifecycle logic.
- **[Limit behavior could diverge between CLI and server]** -> Mitigation: define one 1 through 200 range, validate locally for actionable no-request failures, and validate again on the server boundary.
- **[Plural command removal breaks scripts]** -> Mitigation: this is an intentional breaking change. Return the normal unknown-command failure without a compatibility alias, and update all repository help, hints, examples, and CLI tests in the same change.
- **[Dead-letter credential protections weaken during noun migration]** -> Mitigation: move the existing handlers unchanged under `event`; preserve tests proving non-loopback refusal occurs before credential lookup/request and missing credentials fail before HTTP.
- **[Activity list looks like an event replay]** -> Mitigation: use distinct command/help wording, a bounded normal response rather than NDJSON, and no `event list` path.

## Migration Plan

1. Add the server Activity evidence projection and server specs. Deploying it alone is additive and does not change Web clients using `/events`, `/agent/activity`, or Runner status APIs.
2. Add `ActivityCommands`, its provenance-aware descriptor/table renderer, and CLI specs; register the singular `activity` group.
3. Change the event root command noun to singular, update all affected CLI invocations and root-shape assertions, then remove every plural `events` hint/example reference.
4. Update the CLI reference command map and close its recorded implementation gap. Run `npm test` for server and the CLI test project through the repository's normal .NET test command.

The route addition is additive, but the CLI noun migration is intentionally breaking. Roll back before release by restoring the prior CLI command tree and documentation; the added Activity endpoint can remain harmlessly deployed because it is read-only and has no persistence or schema migration. After release, rollback cannot preserve both the no-alias requirement and old `events` scripts, so a rollback restores the old behavior wholesale rather than adding a compatibility alias.

## Open Questions

None. The Activity list contract is deliberately limited to existing recorded Project Events and existing current Activity snapshots; it adds no storage, event schema, or new aggregate facts.
