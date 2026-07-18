## Context

Issue 413 delivers two things: a reusable, deterministic CEL-subset expression that matches the canonical CloudEvent **envelope**, and the first consumer of it — `mo events tail --match <expr>`. Motivation and the change scope are in [`proposal.md`](../proposal.md); the required behavior is in [`specs/event-envelope-matching/spec.md`](../specs/event-envelope-matching/spec.md) and [`specs/project-event-tail/spec.md`](../specs/project-event-tail/spec.md). The grammar authority is [`design/event-protocol.md`](../../../design/event-protocol.md) (single source for the syntax); this document does not restate the BNF.

Current state that constrains the design:

- The canonical envelope is `CloudEvent` (`Infrastructure/Events/CloudEvent.cs`): `Type`, `Source`, `Subject`, `Id`, `Time`, `SpecVersion`, and `Extensions` (`IReadOnlyDictionary<string,string>`). Lineage (`projectid`, `issue`, `epic`, `workflowrunid`, `stage`, …) is stamped as extensions by #412.
- Live delivery today is SignalR only. `EventBridge` (`Events/Hub/EventBridge.cs`) is a `[Subscription(Type = "com.mohist.*")]` handler that forwards each full envelope to connections chosen by `UserNotificationDispatcher`. The dispatcher's project gate (`UserNotificationDispatcher.cs:371-382`) is **deliberately permissive**: when either the connection or the event lacks `projectid`, it falls back to type-only matching. This is correct for the Web (cross-project/admin tabs) but **violates the tail's strict-isolation requirement**, so the tail must not ride on this shared gate.
- The CLI (`packages/cli`) has no streaming path: every call is a buffered one-shot JSON request through `MohistCliApi`. Project resolution is `TryReadActiveProjectIdAsync()` + `--project`/`--project-id`, with `NoActiveProjectMessage`. The singular `event` noun is registered at `MohistCliCommands.cs:26` and built in `MohistCliCommands.Event.cs:10` (only `dead-letter` subcommands today).
- No parser library exists in the repo; `Microsoft.CodeAnalysis.CSharp` (Roslyn) is present but unrelated. `Microsoft.AspNetCore.SignalR.Client` is centrally versioned but not referenced by the CLI. Regex is plain BCL `System.Text.RegularExpressions`.

## Goals / Non-Goals

**Goals:**

- A reusable, transport-independent compiled matcher over the envelope only, with the exact grammar, operators, functions, presence/missing semantics, compile-time diagnostics, bounded regex, and payload rejection required by `event-envelope-matching`.
- `mo events tail [--match <expr>]` that streams only matching, strictly project-scoped, live envelopes as one compact JSON object per line, with pre-stream validation, cancellation, and best-effort/no-replay semantics.
- A matcher placement that the later routing table and durable dispatch can converge on without rework.

**Non-Goals:**

- Routing table, Agent triggers, response-prompt rendering, dry-run — later issue.
- Numeric/boolean/arithmetic/custom-function CEL; full CEL runtime.
- Durable/replayable tail, persisted query surface, or delivery acknowledgement.
- Changing the Web hub, the permissive dispatcher gate, the finite Activity feed (`GET /api/projects/{ref}/events`), or durable `[Subscription]` dispatch semantics.
- Shipping the matcher to the CLI for local pre-validation.

## Decisions

### D1 — Self-implemented recursive-descent matcher, no external CEL library

The evaluator is a small tokenizer → recursive-descent parser → AST → compiled matcher, ~300–400 LOC plus a conformance suite, in `Infrastructure/Events/Matching`. It depends on a matcher-local envelope view only, not on `CloudEvent` directly.

**Alternatives considered:**
- `Cel` / `Cel.NET`: rejected (per `event-protocol.md`). The target is a flat string→string envelope view; CEL's type system and protobuf integration are unused weight, and neither library is community-mainstream.
- Roslyn scripting / C# expression compilation: rejected — unsafe (arbitrary code surface), heavy startup, wrong tool for a constrained boolean DSL.

### D2 — Matcher reads an `EventMatchInput` view; compile rejects `event.data`

The matcher evaluates against a minimal view:

```
EventMatchInput
  string GetValue(string attr)      // "" when absent
  bool   Has(string attr)           // extension key presence / core-field presence
```

`event.type`, `event.source`, `event.subject` resolve to core fields; any other `event.<ident>` resolves to the extension of that name. Missing ⇒ `""`. `has()` ⇒ key/field presence, so present-but-empty is distinguishable from absent. An adapter projects `CloudEvent` into this view.

`event.data` is the payload, not an envelope attribute. The parser special-cases the identifier `data` as a **compile error**, satisfying the payload-access prohibition without relying on "it would just resolve to empty".

**Alternatives considered:**
- Resolve `event.data` as an (absent) extension ⇒ silently empty: rejected — hides payload coupling and contradicts the spec's compile-time rejection.

### D3 — Compile is the single authority; regex compiled eagerly with a timeout

`EventMatchExpression.Compile(string source) → Result<EventMatchExpression, MatchDiagnostic>`. Compile parses, type-checks, and **eagerly compiles every `matches` argument as a regex** so invalid patterns fail at compile (not evaluation). The compiled object is immutable and reusable across many events. `MatchDiagnostic` carries a location (offset/line) for the spec's location-reporting requirement.

At evaluation, `matches` runs with an injected timeout (`Regex.IsMatch(value, pattern, options, timeout)`). `RegexMatchTimeoutException` and any other regex runtime failure ⇒ **non-match, never propagated**. Runtime evaluation exceptions in general ⇒ non-match, logged + counted (per `event-envelope-matching` "Runtime evaluation failure is a non-match"). The timeout value is injected (default ~100ms), so tests do not touch a wall clock.

### D4 — Tail transport: a streaming NDJSON endpoint, not SignalR

The tail is a new best-effort, expression-filtered endpoint:

```
GET /api/projects/{projectRef}/events/tail[?match=<expr>]
Response: 200, Content-Type: application/x-ndjson
Body: one compact JSON envelope object per line
```

The server compiles `match` (authority per D3). On compile failure it returns **400 with a structured diagnostic containing the location** before any stream opens — this is how the CLI satisfies "validated before streaming begins". The endpoint streams until the client disconnects or cancels; cancellation terminates the request and releases the subscription.

Feed into the tail comes from a **new `[Subscription(Type = "com.mohist.*")]` handler** `EventTailSource` (a sibling of `EventBridge`) that pushes each envelope to active tail channels. Each open tail owns a bounded `Channel<CloudEvent>`; the handler applies **strict project isolation** (event `projectid` must equal the tail's resolved project; absent `projectid` ⇒ skip) and then the compiled expression, and writes non-blocking (drop-on-full, matching best-effort/no-replay). The endpoint reads its channel and writes one compact line per envelope (core fields + extensions; no payload).

**Why NDJSON over SignalR for the tail:**
- Strict isolation is a one-line filter in `EventTailSource`. Reusing SignalR would force strict semantics onto the shared dispatcher gate / connection registry that the Web depends on — exactly the permissive path the tail must not reuse.
- Line-delimited output is the spec's required wire shape; a raw HTTP stream maps to it directly.
- Zero new CLI dependency: `HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)` + a newline reader. The CLI already has `HttpClient`; the `mo` tool stays light (no SignalR client).
- Cancellation = cancel the HTTP request; no reconnect/replay semantics to reason about (the tail is explicitly non-replaying).

**Alternatives considered:**
- Reuse `/hubs/events` + a new hub method + a separate per-connection tail registry with a strict gate: keeps one transport but adds a heavyweight SignalR client to the CLI, contaminates the Web hub's connection model with transient operator state, and brings auto-reconnect semantics that conflict with "best-effort, no replay". Rejected.
- gRPC bidi stream: overkill for a one-way filtered fan-out; adds a new RPC stack.

### D5 — CLI: first streaming reader; `events` noun consolidation is breaking

The CLI adds a small shared streaming helper (headers-read `SendAsync` + line reader) used only by `events tail`. Flow: resolve project locally (`TryReadActiveProjectIdAsync` / `--project`), build the request with `?match=` when supplied, open the stream, print each line to stdout. On 400, parse the structured diagnostic, print the location to stderr, exit non-zero without emitting. Cancellation: a `CancellationTokenSource` linked to `Console.CancelKeyPress` cancels the request and the helper drains and exits cleanly.

The `event` noun (`MohistCliCommands.Event.cs:10`) becomes `events`, gains the `tail` subcommand, and keeps the `dead-letter` subtree. Registration at `MohistCliCommands.cs:26` is unchanged in shape. The singular `mo event ...` simply stops resolving (no compat shim — the project is in active development).

### D6 — Single compile authority is the server; the CLI does not compile

The CLI forwards `match` verbatim and treats the server's 400 as the validation result. This avoids duplicating the parser in two packages and the drift that follows. The CLI's only local check is project resolution (fail-fast without contacting the server when no project is selected).

## Risks / Trade-offs

- **[Second live transport alongside SignalR]** → The tail is a distinct consumer (operator CLI, expression-filtered, strict, NDJSON) sharing the same matcher semantics and the same `[Subscription]` fan-in as `EventBridge`. It is not a second durable channel and does not touch the Web hub or the dispatcher gate.
- **[Process-local tail source misses events on other silos]** → Single daemon today (`design/architecture.md`). Tails only see events delivered to their silo's `EventTailSource` activation. Acceptable for best-effort; revisit when the dispatcher is sharded.
- **[Regex catastrophic backtracking]** → Eager compile-time validation + per-match injected timeout + timeout-as-non-match; deterministic in outcome (false), never an unhandled throw.
- **[Tail channel backpressure]** → Bounded channel, non-blocking `TryWrite`; a full channel drops (best-effort), matching the no-replay/no-durability contract rather than blocking dispatch.
- **[Tail handler must not stall durable dispatch]** → `EventTailSource` does only cheap, non-blocking work per event; bounded-channel drop-on-full guarantees it cannot block the dispatcher fan-out.
- **[Matcher grammar drift from CEL]** → A conformance test set pins accepted syntax; the grammar authority remains `design/event-protocol.md`.
- **[CLI noun rename breaks scripts/docs]** → Intentional **BREAKING**; docs/cli-reference, skill-data, and the existing dead-letter specs are updated in this change.

## Migration Plan

This change is additive on the server (new endpoint, new handler, new matcher module) except for the CLI noun rename; there is no persisted-state change and no data migration.

1. Add the matcher module + unit conformance suite (grammar, precedence, every operator/function, missing-attr, `has()`, eager regex-reject, timeout-as-non-match via injected timeout, `event.data` rejection, determinism).
2. Add `EventTailSource` + the `events/tail` endpoint + an injectable `IEventTailSource` seam so server specs can push envelopes directly without driving the durable dispatcher.
3. Add server specs: strict project isolation (other-project and unprojected events suppressed), expression filter, 400-with-location on invalid `match`, NDJSON line shape, cancellation/release on disconnect.
4. Add the CLI `events tail` streaming reader + helper; rename the `event` noun to `events`; move dead-letter under it.
5. Add CLI specs via the existing fake HTTP factory, extended to return streaming NDJSON and 400 diagnostic bodies; assert stdout lines, stderr location, non-zero exit, and cancellation. Update existing dead-letter specs to `events dead-letter`.
6. Update `docs/cli-reference.md` and skill-data to `mo events ...`.

**Rollback:** revert the change set. No schema or persisted-state migration to undo; the additive endpoint and handler simply disappear. Scripts using the old `mo event` noun break on rollback only if they had already migrated — acceptable in active development.

## Open Questions

- **Tail output payload?** Default is envelope-only (core fields + extensions, no `data`), matching the `event-envelope-matching` envelope-only contract and the output spec's field list. A `--include-data` flag for richer tail output is a candidate follow-up.
- **Default regex timeout value** (proposed ~100ms) and whether it should be operator-configurable. The seam is injected either way, so the value can be finalized during implementation.
- **Multi-silo tail coverage**: out of scope now (single daemon); tracked as a known limitation to revisit with the sharded dispatcher.
