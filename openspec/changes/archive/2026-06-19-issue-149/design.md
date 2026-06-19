## Context

Issue labels are a flat `string[]` on the `Issue` aggregate today, threaded unchanged through domain, persistence, API, Web, and CLI. We are replacing that primitive with a K8s-style key-value model where each key maps to at most one value. See `proposal.md` for motivation and `specs/` for the contract.

Current state (touch points confirmed in code):
- **Domain**: `Issue.cs:10,37` backing field `_labels` + `Labels` getter returns a copy `[.. _labels]`; `Issue.Transitions.cs:13-53` `Create`/`Update` take `string[]? labels` and detect change via `SequenceEqual`; `IssueEvent.cs:16-25` `IssueCreated.Labels` / `IssueLabelsChanged(OldLabels, NewLabels)` are `string[]`.
- **Persistence**: `IssueRow` (`Infrastructure/Data/Issue/IssueRow.cs`) stores the **entire aggregate as JSON `State`** via `IssueStore.Serialize/Deserialize`. There is **no dedicated `labels` column** — labels live inside `State`. `IssueQuerier.ListAsync` filters in-memory with `i.Labels.Contains(label, ...)` (`IssueQuerier.cs:99`).
- **Grain/API**: `IIssueGrain.CreateAsync(... string[]? labels ...)` (`IIssueGrain.cs:9`), `[GenerateSerializer] UpdateIssueData` has `[property: Id(2)] string[]? Labels` (`IssueGrain.cs:545`), `UpdateFullAsync` calls `_issue.Update(... data.Labels ...)` (`IssueGrain.cs:332`). `IssueInfo.Labels` / `IssueReadModel.Labels` are `string[]`. `Api/LabelsRoutes.cs` returns a hardcoded `DefaultLabels` list.
- **Events**: `IssueEventSerializer` maps `IssueLabelsChanged` → `com.mohist.issue.labels-changed` and serializes the record via System.Text.Json (`IssueEventSerializer.cs:26,39`). Only the payload shape changes; the bus type string stays.
- **Web**: `entities/issue/model/types.ts:155` `labels: string[]`; `widgets/kanban-board/model/board-query.ts` `BoardQueryState.labels: string[]` + `applyBoardFilters` does `issue.labels.includes(label)`; `api/client.ts:123` `getLabels(): string[]`; Create/Edit dialogs toggle flat chips; `entities/issue/@x/events.ts:54` maps the labels-changed payload as `{ labels: string[] }`.
- **CLI**: `MohistCliCommands.LabelOption()` returns `Option<string[]?>` `--label/-l` (`MohistCliCommands.cs:67`); issue commands consume it (`MohistCliCommands.Issue.cs`).
- **Reference pattern**: `AgentSessionMetadata` (`Sessions/Domain/AgentSession.cs:42-70`) already uses `IReadOnlyDictionary<string,string>? Labels` with a `WithLabel(key,value)` upsert helper and `[GenerateSerializer]`. We mirror this exactly for consistency.

Constraints: project is pre-release (no real data to protect); no historical flat-label migration is required; no new aggregate; no workflow/auth/runtime impact; risk rated medium because it touches the core aggregate field type across many layers, but it is fundamentally a localized type replacement.

## Goals / Non-Goals

**Goals:**
- Replace `Issue.Labels` `string[]` with a single-value key-value map end-to-end (domain → store → API → Web → CLI → events).
- Enforce key/value validation in one authoritative place (the domain).
- Provide key-addressed `SetLabel` / `RemoveLabel` aggregate operations and an `IssueLabelsChanged` event carrying before/after maps.
- Land the change with **no DB schema migration** and **no historical data migration**.

**Non-Goals:**
- Board swim-laning / grouping by label key (epic #8 child #2).
- Labels on Epics (epic #8 child #3).
- A managed `stream` vocabulary (epic #8 child #4).
- Multi-value-per-key labels.
- Label metadata (color / description / order).
- Migrating existing flat labels into key-value form.

## Decisions

### D1. Domain storage: `Dictionary<string,string>` (ordinal) with a partial `Issue.Labels.cs`, mirroring `AgentSessionMetadata`
Back the labels with a `Dictionary<string,string>(StringComparer.Ordinal)` field, exposed as `IReadOnlyDictionary<string,string>`. Put `SetLabel`, `RemoveLabel`, `ReplaceLabels`, and validation in a new partial `Issue/Domain/Issue.Labels.cs` (the issue's stated code drop point). `Labels` getter returns the dictionary as-is (immutable view) rather than a per-call array copy.

- **Alternatives considered**:
  - A dedicated `IssueLabels` value object wrapping the map + invariants. Rejected: adds a type for little gain over a partial; conflicts with the "keep data models minimal" principle and with the inlined approach already used by `AgentSessionMetadata`.
  - `ImmutableDictionary<string,string>`. Rejected: `AgentSessionMetadata` uses a mutable-backed `IReadOnlyDictionary`; matching it keeps one mental model.
- **Rationale**: one mental model for "labels map" across the codebase; fewest new types; ordinal comparer matches the existing session-labels behavior.

### D2. Validate in the domain, translate errors at each surface
Key must match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`; value must be non-empty/non-whitespace. `SetLabel`/`ReplaceLabels`/`Create`/`Update` validate and throw `ArgumentException` with a clear message. API maps that to HTTP 400; CLI to exit code 1 with the message; Web to an inline editor error before submit.

- **Alternatives considered**:
  - Validate only at the API boundary. Rejected: the invariant would not be enforced for grain/test/internal callers, violating the aggregate's encapsulation.
- **Rationale**: single source of truth; the acceptance criterion "非法输入被拒并给清晰错误" holds regardless of entry point.

### D3. Storage: no schema migration; whole-state JSON, tolerant of legacy array-form labels
Because `IssueRow.State` is the whole aggregate JSON, changing the domain type flows through `IssueStore.Serialize/Deserialize` automatically — no column, no migration. The one wrinkle: existing dev rows serialize `labels` as a JSON array; deserializing an array into `Dictionary<string,string>` throws. We make the read path **tolerant**: if `labels` is absent or a non-object, normalize to an empty map (silently discarding legacy flat labels, which is permitted by the non-goal). Implement via a small JSON converter or a post-deserialize normalization on `Issue`.

- **Alternatives considered**:
  - Hard cutover — let legacy rows throw, force a dev DB reset. Rejected: a crash on load is a poor first-run experience even for a dev project; tolerance is ~10 lines.
  - A real migration rewriting arrays into `{<label>: <label>}` objects. Rejected: explicitly out of scope and semantically lossy.
- **Rationale**: zero-migration cutover that still boots on old data.

### D4. Event shape changes, bus type string unchanged
`IssueLabelsChanged(IReadOnlyDictionary<string,string> OldLabels, NewLabels)` and `IssueCreated(... IReadOnlyDictionary<string,string>? Labels ...)`. Keep the CloudEvents type `com.mohist.issue.labels-changed` (`IssueEventSerializer.cs:26`); only the payload changes. Web event handler / describe text render `key=value`.

- **Alternatives considered**:
  - Version the bus type (e.g. `...labels-changed.v2`). Rejected: no live consumers depend on the old array shape; versioning adds overhead for no benefit in a pre-release.
- **Rationale**: smallest diff; the Web consumer is updated in the same change.

### D5. HTTP contract: full-replacement `labels` object on existing PATCH; no new endpoints
`POST /api/issues` and `PATCH /api/issues/:id` accept `labels` as a JSON object (full replacement on update, matching today's whole-array replace semantics). We do **not** add dedicated `POST/DELETE /labels/:key` endpoints. The domain still implements `SetLabel`/`RemoveLabel` as the aggregate primitives (used by specs/tests and available for future granular endpoints).

- **Alternatives considered**:
  - Granular endpoints (`POST /api/issues/:id/labels` set, `DELETE .../labels/:key`). Rejected for now: more surface area, routing, and serialization for a low-concurrency dev tool; full replacement already expresses set (include key) and remove (omit key).
- **Rationale**: minimal API surface, consistent with current update path, satisfies the spec scenarios. Granular endpoints remain a future option if concurrency or UX demands it.

### D6. CLI flag form: `-l key=value` to set, `-l -key` to remove
Keys cannot start with `-` (validation), so a leading `-` unambiguously denotes removal. Parse each `-l` token: split on the first `=` → set; leading `-` → remove by key; reject malformed tokens (`-l =x`, `-l key=` empty value, bad key) with exit 1. The CLI applies the `+/-` delta against the issue's current labels and sends the resulting **full map** via PATCH (read-modify-write at the CLI).

- **Alternatives considered**:
  - Add a granular API so the CLI sends only the delta (avoiding read-modify-write). Rejected: ties CLI to a new endpoint we otherwise don't need (see D5); the race is acceptable for a single-user dev tool.
  - `-l key:value` (colon). Rejected: `=` is the conventional `key=value` form cited in the issue.
- **Rationale**: matches the issue's `key=value` requirement; unambiguous given key validation; reuses the full-replacement PATCH.

### D7. Web filter model: `key=value` tokens; value options derived client-side
`BoardQueryState.labels` stays a `string[]` but each entry is now a `key=value` token. `applyBoardFilters` matches an issue when `issue.labels[k] === v` for every selected token. The filter UI derives the selectable `key=value` pairs from the **already-loaded issues' label maps** (client-side), so it is not constrained by what `GET /api/labels` returns. URL serialization (`labels=k1=v1,k2=v2`) is unchanged in shape.

- `GET /api/labels` returns distinct **keys** (per spec) — this serves `mo label list` and the "reach all label keys" requirement; the Web filter does not depend on it for value options.
- **Alternatives considered**:
  - Make `GET /api/labels` return distinct `key=value` pairs to drive the filter. Rejected: would diverge from the spec'd "keys" contract; client-side derivation from loaded issues already gives accurate, scoped pairs and avoids an extra coupling.
- **Rationale**: keeps the listing contract (keys) and the filter contract (pairs) clean and decoupled.

## Risks / Trade-offs

- `[Legacy JSON array labels crash deserialization]` -> Tolerant read path normalizes non-object `labels` to an empty map (D3). Covered by a deserialization spec.
- `[CLI read-modify-write race on concurrent label edits]` -> Accepted; single-user dev tool, low concurrency. Mitigated by re-reading current labels at edit time; granular API is the documented future fix (D5/D6).
- `[Board filter URL with `=` inside values collides with the `key=value` token grammar]` -> Constrain: value is matched literally up to the first `=`; if a value needs an `=`, document that the token splits on the first `=` only. Low risk for typical values like `frontend`, `auth`.
- `[Forgetting a surface leaves a dangling `string[]` that silently compiles after JSON reshaping]` -> The type change from `string[]` to `IReadOnlyDictionary<string,string>` is a compile-time break in C#; use the compiler to enumerate call sites. On the Web/TS side the type change is softer — add an explicit `Record<string,string>` type and grep for `.labels` usages; cover with the updated component/unit tests.
- `[`IssueLabelsChanged` consumers outside this repo expect the array payload]` -> None known (pre-release, single consumer updated here). Noted for completeness.
- `[Over-constraining keys blocks a future need for uppercase/unicode keys]` -> Accepted; the K8s-style grammar is intentional and matches the issue; loosening later is backward-compatible.

## Migration Plan

1. **Domain first**: change `Issue.Labels` type + event records + add `Issue.Labels.cs` operations/validation. Compiler errors enumerate every C# call site.
2. **Persistence**: add tolerant `labels` handling in `IssueStore.Deserialize` (D3); no EF migration, no `ModelSnapshot` change (no column involved). Verify `IssueRow`/`MohistDbContext` need no edits (they should not).
3. **Grain/API**: update `IIssueGrain.CreateAsync` signature, `UpdateIssueData` serializer field, `IssueInfo`/`IssueReadModel` types, `IssueQuerier` filter (`map[key] === value` instead of `.Contains`), and `LabelsRoutes` to return distinct keys from stored issues (drop the hardcoded list).
4. **Events**: `IssueEventSerializer` payload flows automatically via System.Text.Json; update Web event mapping + describe text.
5. **Web**: update `types.ts`, `board-query.ts` filter, dialogs to key+value editor, `getLabels`/`useLabels`, event timeline.
6. **CLI**: `-l key=value` / `-l -key` parsing in `LabelOption` consumers.
7. **Tests**: update `IssueDomainSpecs`, `IssueQuerierSpecs`, `IssueTestData`, Web `CreateIssueDialog.test.tsx`, `kanban-grouping.test.ts`, `describe.test.ts`; add validation + tolerant-deserialize specs.
8. **Rollback**: revert the commit(s). Since there is no migration, rollback restores the previous array shape cleanly; any rows written in key-value form during the window are dev data and can be discarded. No remote/DB rollback procedure required.

## Open Questions

- Should `mo label list` / `GET /api/labels` eventually surface `key=value` pairs (for richer discovery) rather than keys only? Current spec says keys; revisit if the board filter or future swim-laning needs server-side pairs. (D7 keeps the Web filter independent of this answer.)
- Do we want the CLI to print a hint when discarding legacy flat labels on load, or stay silent? Current design is silent (non-goal = no migration); a one-line stderr note on first load is a low-cost nicety to decide during build.
- Granular label set/remove HTTP endpoints (D5) — defer until a concrete concurrency/UX need appears; tracked as a candidate epic #8 child.
