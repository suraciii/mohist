## Context

Mohist workflows currently build a complete variable payload once per dispatch in `WorkflowGrain.MakeDispatchAsync`. The payload merges embedded template variables, project/issue profile variables, and dispatch-injected metadata (`workflow.*`, `stage.*`, `work.*`), then renders task `with` values against that bundle. `ActionResult.output` exists on the runner but is only used for check-result parsing; `WorkResult.Output` travels back to the server but is not consumed for variable storage.

This means tasks cannot communicate dynamic values to downstream tasks. Every path, name, or computed value must either be hardcoded at profile time or smuggled through disk artifacts. The issue asks for a first-class runtime variable channel: tasks declare outputs, the runner captures them from the action JSON result, the server stores them on `WorkflowRun`, and subsequent dispatches resolve them through the existing `${{ }}` syntax as `${{ tasks.<id>.outputs.<name> }}`.

## Goals / Non-Goals

**Goals:**
- Allow `TaskDefinition` to optionally declare an `outputs` array of `{ name, from }` entries.
- Runner parses `ActionResult.output` as JSON and extracts declared outputs on successful task completion.
- Server stores captured outputs in a runtime variable store attached to `WorkflowRun`, keyed by `tasks.<id>.outputs.<name>`.
- `MakeDispatchAsync` deep-merges the runtime store into the variable resolution chain after dispatch injection and before final template rendering.
- Template resolution supports `${{ tasks.<id>.outputs.<name> }}` in task `with` and `artifacts`.
- Runtime variables persist across stage transitions within the same workflow run.
- Failed tasks produce no output variables; tasks without `outputs` behave exactly as before.

**Non-Goals:**
- External runtime variable injection.
- Cross-workflow variable sharing.
- Mutable variables after capture (write-once semantics).
- Replacing the existing `openspecChangeDir` hardcoding (covered separately).

## Decisions

### 1. Store runtime variables directly on `WorkflowRun`

Runtime variables will live as a new `Dictionary<string, JsonElement>` (or nested `JsonElement` object under a `tasks` root) on the `WorkflowRun` aggregate state, populated through `WorkflowRun.CompleteTask` or a dedicated domain method.

**Rationale:**
- Keeps the runtime store in the same transactional boundary as task completion events.
- Orleans serialization already covers the aggregate; no extra grain or persistence table is needed.
- Stage transitions and retries naturally inherit the store because the same `WorkflowRun` grain is reused.

**Alternatives considered:**
- Separate `WorkflowRuntimeVariablesGrain`: adds a distributed coordination point and extra persistence without clear benefit.
- `WorkflowVariablesRow` table: useful for static profile variables, but runtime vars are transient per-run and belong with the run state.

### 2. Output declaration uses `{ name, from }` with JSONPath-style selectors

`TaskDefinition` will gain:
```csharp
public sealed record TaskOutputDefinition(string Name, string From);
public sealed record TaskDefinition(
    ...,
    List<TaskOutputDefinition>? Outputs = null);
```

`from` is a dotted selector evaluated against the action result, where `output` is the top-level field holding the action's JSON output (e.g. `output.openspecName` selects `ActionResult.output.openspecName`). The runner parses `ActionResult.output` as JSON and resolves the selector against the action result object.

**Rationale:**
- Simple dotted paths cover the immediate needs without pulling in a full JSONPath library.
- Keeps YAML readable and matches the `${{ tasks.proposal.outputs.openspecName }}` reference style.
- Can be extended to true JSONPath later if nested/array access becomes necessary.

**Alternatives considered:**
- Full JSONPath (`$.output.openspecName`): more powerful but heavier dependency and steeper for users.
- Direct field name only: too restrictive for structured outputs.

### 3. Runner extracts outputs and reports them in `WorkResult`

The runner will add a `CapturedOutputs` dictionary to the `WorkResult` it reports, or reuse the existing `Output` field by parsing and extracting before reporting. The preferred design is to extend `WorkResult` with an explicit `IReadOnlyDictionary<string, JsonElement> CapturedOutputs` so the server does not need to re-parse or trust raw JSON strings.

**Rationale:**
- Clear contract between runner and server; server validates declared outputs against task definition.
- Failed tasks simply omit `CapturedOutputs`, matching the "no outputs on failure" requirement.

**Alternatives considered:**
- Server parses `WorkResult.Output` itself: couples parsing logic to the server and duplicates runner-side JSON handling.

### 4. Server-side output capture is declared-definition driven

`ProcessTaskResultAsync` will look up the current task's `TaskDefinition`, validate that reported output names match declared names, and apply only those values to the runtime store. Undefined outputs are ignored.

**Rationale:**
- Prevents accidental namespace pollution and keeps the runtime store predictable.
- Makes the `outputs` declaration the source of truth for what downstream tasks can reference.

**Alternatives considered:**
- Capture all action output fields automatically: simpler for authors but breaks the explicit contract and can leak internal action data.

### 5. Runtime variables merge after dispatch injection in `MakeDispatchAsync`

The existing resolution chain becomes:
```
embedded vars -> profile vars -> stage overlay -> dispatch scope (workflow/stage/work) -> runtime task outputs -> final vars
```

`MakeDispatchAsync` will add the runtime store as a top-level `tasks` object in the variable payload before rendering `with` and `artifacts` templates, so `${{ tasks.<id>.outputs.<name> }}` resolves correctly. It will not be nested under `vars`.

**Rationale:**
- Runtime outputs should override static values when names collide, because they represent the freshest run-specific state.
- Minimal change to the existing chain: the runtime store is just another merged layer.

**Alternatives considered:**
- Merge runtime vars before dispatch scope: would prevent tasks from overriding workflow metadata, which is safer but less useful; the chosen order matches the spec's precedence requirement.

### 6. Write-once semantics with retry overwrite

A given `tasks.<id>.outputs.<name>` key is written on first success and only updated when the same task succeeds again during a retry. Failed attempts leave the stored value untouched.

**Rationale:**
- Matches the spec and avoids surprising mutations from external sources.
- Retries must be able to refresh stale outputs.

## Risks / Trade-offs

- **[Risk]** Output `from` selectors reference missing fields, leaving variables empty silently. -> **Mitigation:** Treat missing values as absent rather than failures; consider optional validation/warnings in a later iteration.
- **[Risk]`WorkResult` schema change breaks runner/server compatibility during deployment. -> **Mitigation:** Add the new field as optional; older runners omit it and server treats absence as no outputs.
- **[Risk]** Large `ActionResult.output` payloads bloat the runtime store and grain state. -> **Mitigation:** Only store declared outputs, not the full output JSON; add documentation guidance to declare only needed values.
- **[Risk]** Deep-merging runtime vars after dispatch scope changes precedence behavior in subtle ways for existing workflows. -> **Mitigation:** Only keys under `tasks.*.outputs.*` are introduced; no existing top-level keys are shadowed unless a user intentionally uses the new namespace.

## Migration Plan

1. **Code changes:**
   - Add `TaskOutputDefinition` and `Outputs` property to `TaskDefinition`.
   - Extend `WorkResult` with optional captured outputs.
   - Add runtime variable store to `WorkflowRun` and domain method to append outputs on task completion.
   - Update runner to parse `ActionResult.output` and extract declared outputs.
   - Update `WorkflowGrain.MakeDispatchAsync` to merge runtime variables into the payload.
   - Update YAML parser/validator to accept and validate `outputs`.

2. **Persistence:**
   - `WorkflowRun` state change is handled by Orleans grain state serialization; no database migration is required unless grain state is version-sensitive. If so, a default-empty store handles backward compatibility.

3. **Deployment:**
   - Deploy server first; it tolerates missing `CapturedOutputs` from older runners.
   - Deploy runner second so new runners can start reporting outputs.

4. **Rollback:**
   - Roll runner back to previous version: server continues to work, runtime store stays empty, behavior reverts to pre-feature state.
   - Roll server back after runner has reported outputs: old server ignores the new `CapturedOutputs` field, so runtime vars are not stored or resolved. No data corruption because outputs are not written to persistent store.

## Open Questions

- Should the `from` selector support bracket/array access (`output.items[0].id`) now or be deferred?
- Should missing declared outputs produce a warning log to help authors debug typos?
- Should `CheckDefinition` also support `outputs`, or is this limited to tasks in this iteration?
- How should output values of non-primitive JSON types (objects/arrays) be rendered in `${{ }}` templates? Stringify, reject, or treat as nested objects?
