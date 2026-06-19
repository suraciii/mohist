## Why

Issue labels are a flat `string[]` today, so a token like `frontend` is ambiguous — it could be a stream, a team, or an area, and the system has no way to know which dimension it is. This blocks treating labels as a real classification system and directly holds back epic #8's value-stream dimension. Moving labels to a key-value (single value per key) model — K8s-style `{key: value}` — lets users classify work along independent dimensions (`stream:frontend`, `module:auth`) without collisions or semantic blur. This is a primitive enhancement on the existing Issue aggregate, not a new aggregate, and the project is pre-release so no historical flat-label migration is needed.

## What Changes

- **BREAKING:** `Issue.Labels` changes type from `string[]` to a key-value single-value model (`IReadOnlyDictionary<string,string>`-equivalent). Each key has at most one value.
- **BREAKING:** Label operations move from string add/remove to key-addressed `SetLabel(key, value)` (upsert) and `RemoveLabel(key)`, plus full replacement.
- **Add key/value validation:** keys match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$` (lowercase alphanumerics + short dashes); values are arbitrary non-empty strings. Invalid input is rejected with a clear error.
- **`IssueLabelsChanged` event** carries old and new maps (replacing the old/new `string[]` pair).
- **HTTP API** supports set/remove of key-value labels (replacing flat label acceptance on issue create/update).
- **Web** Create/Edit label editors accept `key` + `value` pairs instead of toggling flat chips.
- **CLI** label parameters accept `key=value` form (replacing bare `-l label` / `-l +bug` / `-l -bug`).
- **No historical flat-label migration.** Existing flat labels may be discarded or treated as invalid; migration details are deferred to the Plan phase.
- **Out of scope (non-goals):** board swim-laning/grouping by label key; adding labels to Epics; a managed `stream` vocabulary; multi-value-per-key labels; label metadata (color/description/order).

## Capabilities

### New Capabilities
- `issue-labels`: The contract for the Issue label primitive as a key-value (single-value-per-key) model — the label shape, key/value validation rules and rejection behavior, the `SetLabel`/`RemoveLabel`/full-replacement operations, the single-value-per-key invariant, and the `IssueLabelsChanged` event carrying old/new maps. This primitive is referenced by the store, API, Web, and CLI surfaces but owned in one place. Becomes `specs/issue-labels/spec.md`.

### Modified Capabilities
- `local-issue-store`: Label persistence and CLI add/remove/list scenarios change from a flat `string[]` (JSON array column) to the key-value map; the `mo issue update -l +bug/-bug` and `mo label list` scenarios SHALL be updated to reflect key-addressed set/remove and the key-value listing shape.
- `http-api`: The `POST /api/issues` / `PATCH /api/issues/:id` `labels` field and `GET /api/labels` change shape from flat strings to key-value label set/remove and key(-value) listing.
- `web-ui`: The Kanban label filter and Create/Edit label editor scenarios change from flat chips to key+value entry and key(-value) based filtering.
- `cli-interface`: The `mo issue create/update` label parameter (`-l`) and `GET /api/labels` scenario change from flat label tokens to `key=value` form.

## Impact

- **Domain** (`packages/server/src/Mohist.Server/Issue/Domain/`): `Issue.cs` `Labels` field type, `Issue.Transitions.cs` `Create`/`Update` signatures and label-change detection, `IssueEvent.cs` `IssueLabelsChanged` record shape (and `IssueCreated.Labels`). New `SetLabel`/`RemoveLabel` operations likely land in a partial `Issue.Labels.cs`.
- **Persistence** (`packages/server/.../Infrastructure/Data/`): the `issues.labels` column serialization moves from JSON array to JSON object; `IssueQuerier` label filter (`Labels.Contains`) adapts to the map.
- **Grain / API** (`IssueGrain.cs`, `IIssueGrain.cs`, `Api/LabelsRoutes.cs`, `MohistApiRegistration.cs`): create/update payloads and the labels route move to key-value; `IssueInfo`/`IssueReadModel` `Labels` property type.
- **Event serialization** (`IssueEventSerializer.cs`): `IssueLabelsChanged` payload encoding updates for old/new maps.
- **Web** (`packages/web/src/`): `entities/issue/model/types.ts` `labels: string[]`; `api/client.ts` `getLabels()`; `api/queries.ts` `useLabels()`; `widgets/kanban-board/model/board-query.ts` `applyBoardFilters`; `features/edit-issue/ui/EditIssueDialog.tsx` and `features/create-issue/ui/CreateIssueDialog.tsx`; event timeline describe mapping (`entities/issue/@x/events.ts`, `widgets/issue-event-timeline/model/describe.ts`).
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`, `MohistCliCommands.cs` `LabelOption`): label option parsing moves to `key=value`.
- **Tests**: `IssueDomainSpecs.cs`, `IssueQuerierSpecs.cs`, `IssueTestData.cs`, Web `CreateIssueDialog.test.tsx`, `kanban-grouping.test.ts`, `describe.test.ts` updated to the new shape.
- **No workflow / auth / runtime impact.** No new dependencies. Risk is medium: it touches the core aggregate field type across many layers, but the change is a localized type replacement with no migration burden.
