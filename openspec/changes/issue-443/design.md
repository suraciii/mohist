## Context

The [proposal](proposal.md) establishes the motivation, and the [action-output specification](specs/action-output/spec.md) is the normative behavior contract. Workflow Action output currently crosses the runner as `string | null`. Built-in Actions serialize objects themselves, `core/process` returns bare stdout, and `setVars`, recovery, dynamic artifact capture, the server lifecycle, and the Web UI each parse that string independently. The server eventually stores a `JsonElement?` in `TaskRun.Output`, but invalid JSON is wrapped as a JSON string and non-object values are silently omitted from `tasks.<id>.outputs`.

The shared result envelope also carries non-Action results: check dispatches put a serialized array in `WorkItemResult.output`, and AgentJobs put their own serialized terminal result there without crossing the Workflow Action boundary. The implementation must therefore distinguish the Action contract from the shared transport type instead of narrowing every result to an object.

This change spans the runner, runner HTTP contract, Orleans messages, Workflow persistence/read models, and Web task details. Server and runner versions are deployed together in this actively developed project; no old wire-format compatibility layer or persisted-data rewrite is required.

## Goals / Non-Goals

**Goals:**

- Make `JsonObject | null` the runner-internal public success-output type for every Action.
- Validate the object-or-null and JSON-serializability invariant once at the Action execution boundary and fail invalid results explicitly.
- Preserve structured output through runner reporting, Workflow report translation, `TaskRun.Output`, task-output references, recovery, `setVars`, and task-detail APIs without nested JSON strings.
- Change `core/process` success output to `{ stdout, exitCode }` while preserving every other built-in Action's field contract.
- Keep check batches and AgentJob results working through the shared report envelope without treating them as Workflow Action output.

**Non-Goals:**

- Action manifests, input validation, output-field declarations, or runtime validation against an Action-specific output schema.
- New Workflow YAML syntax or changes to missing task-reference rendering.
- Making the currently disconnected `capturedOutputs` field authoritative or adding a server-side output declaration model.
- Changing AgentJob terminal-output or check-view product semantics.
- Rewriting historical `TaskRun.Output` values or supporting old runners after deployment.

## Decisions

### 1. Validate structured output at the Action boundary

`ActionResult.output` becomes `JsonObject | null`, and `succeed` accepts only that type. All built-in Actions return objects directly; their tests assert objects directly instead of parsing strings. `core/process` returns `{ stdout: result.stdout.trim(), exitCode: result.exitCode }`. The existing top-level `WorkItemResult.exitCode` remains an execution fact; `core/process.output.exitCode` is intentionally also public because workflow expressions and `setVars` cannot read runner-private facts.

A shared runtime validator used by task and check execution verifies that a successful result has an object root or `null` and recursively contains only JSON values (including finite numbers and no cycles, `undefined`, functions, or `bigint`). An invalid result becomes a normal failed result with an actionable `invalid-action-output` error and no output. Validation happens immediately after Action invocation and private-fact removal, before completion evaluation, recovery matching, artifact capture, or `setVars`; a default error recovery handler may handle this failure, but `output.*` cannot match it.

Alternative considered: rely only on TypeScript types. Rejected because plugin implementations and runtime values can bypass static typing, and serialization can otherwise silently drop or coerce invalid values.

Alternative considered: keep `string` and centralize parsing in one helper. Rejected because the wire and persistence boundaries would still encode an object as text and could still accept ambiguous plain text.

### 2. Keep the shared report envelope generic JSON

`WorkItemResult.output` becomes `JsonValue | null`, while Workflow Action results remain the narrower `JsonObject | null`. This accommodates three existing variants without inventing a new transport wrapper:

- Workflow task: Action object or `null`.
- Checks: a structured array of check-result rows whose individual Action outputs are objects or `null`.
- AgentJob: its existing non-Action terminal output representation.

`ServerConnection.report` sends this value as the JSON `output` property. On the server, `RunnerReportRequest.Output` and `WorkResult.Output` become `JsonElement?`. `WorkflowItemTranslator` validates Workflow task output as object-or-null and canonicalizes an explicit JSON `null` to nullable `null` before creating `TaskReport`; an invalid successful task report is converted to a durable failed task report and acknowledged, preventing an incompatible runner from retrying forever. Check translation requires an array and reads it directly. The AgentJob adapter handles its own representation and does not pass through Workflow task validation.

Alternative considered: make shared `WorkItemResult.output` object-only. Rejected because check batches are arrays and AgentJobs do not implement the Action contract.

Alternative considered: introduce separate report endpoints or a discriminated result hierarchy for tasks, checks, and AgentJobs. This would make the transport stricter, but it is a larger protocol refactor than required; work kind already provides the boundary needed for validation.

### 3. Remove output parsing from runner consumers

`set-vars`, recovery, and dynamic artifact discovery receive the Action output object directly:

- `extractSetVars` traverses the object and builds the complete Run Variables patch before issuing one PATCH request. A missing source path or `null` output returns an error before any write, preserving atomic projection.
- Recovery constructs `{ output, error }` using the object directly; `when: output.*` and `${{ failure.output.* }}` retain their existing path and type semantics.
- `actionProducedArtifacts` reads fields directly and contains no `JSON.parse` fallback.

The existing `RenderedWorkItem.outputs` / `capturedOutputs` path remains non-authoritative and is not wired into the server as part of this issue. `tasks.<id>.outputs` continues to mean the entire persisted task output object.

Alternative considered: retain tolerant parsing for callers that still return serialized JSON. Rejected because it would preserve two valid encodings and recreate the silent failure this change removes.

### 4. Store and project Workflow output without conversion

`TaskReport.Output` becomes `JsonElement?`. `WorkflowWorkLifecycle` clones/assigns the element directly to `TaskRun.Output` and deletes `ParseOutputToJsonElement`, including its string-wrapping fallback. `WorkflowItemTranslator` is the server-side defense-in-depth authority that prevents new scalar or array Workflow task outputs from reaching persistence.

`MergeTaskOutputsIntoPayload` continues to expose only completed tasks, but reads the already-object-valued `TaskRun.Output` and places it under `tasks.<definitionId>.outputs` without text conversion. Recovery follow-up context uses the same stored element. Historical non-object values are not rewritten and are not projected as valid task outputs.

String-oriented secondary consumers must inspect `JsonValueKind` explicitly. In particular, approval-feedback resolution must not stringify an arbitrary object into human-readable summary text; historical string values may remain readable, while new object output supplies no generic summary unless that flow defines an Action-specific field in a separate change.

Alternative considered: keep `TaskReport.Output` as `string` and parse only in the lifecycle. Rejected because it leaves the runner/server contract ambiguous and makes the server responsible for recovering structure.

### 5. Adapt checks and AgentJobs at their domain boundaries

Check execution returns the result-row array directly in `WorkItemResult.output`; each passing row carries the Action's object-or-null output. `ParseCheckResults` accepts a `JsonElement` array and no longer parses an outer string. `StageCheck.Output` remains `JsonElement?`; check output is still not added to `CheckStatusView` by this issue.

AgentJobs continue to bypass `ActionResult`. Their current terminal-output contract is preserved at the Agent boundary: the shared `JsonElement?` is converted only as required by `AgentJobTerminalResult`, and failure-category inspection operates on the inbound value without routing through Workflow output helpers. No Workflow object-root validation is applied to AgentJob reports.

Alternative considered: migrate `AgentJobTerminalResult.Output` and its API to structured JSON in the same change. Rejected because AgentJob output is a separate domain contract and the issue requires only Workflow Action output.

### 6. Expose structured task output to Web clients

`TaskStatusView.Output` becomes `JsonElement?`; `WorkflowStatusMapper` passes object-valued `TaskRun.Output` directly and maps historical non-object values to `null`, so ASP.NET serializes valid output as a nested JSON object rather than a JSON string. Web timeline/task types become `Record<string, unknown> | null` (or the equivalent generated API type), and `parseTimelineTaskOutput`, `parseTaskOutput`, and string fallbacks in task-output consumers are removed. Existing structured-output renderers receive the object unchanged.

Alternative considered: keep the public API field as a string and parse only once in a shared Web helper. Rejected because the API would remain inconsistent with persistence and every non-Web client would still need to recover the object.

## Risks / Trade-offs

- [Runner and server wire formats are mutually incompatible during rollout] -> Stop/drain the runner, deploy server and runner together, then restart dispatch; do not support mixed versions.
- [A missed built-in `JSON.stringify` or test-side parser hides a remaining string producer] -> Change `ActionResult.output` first so typecheck identifies producers, then sweep production and tests for output-specific `JSON.parse`/`JSON.stringify` calls.
- [Shared `WorkResult.Output` changes accidentally alter checks or AgentJobs] -> Test each work kind independently and keep validation in `WorkflowItemTranslator`/Agent adapters rather than in the generic HTTP route.
- [Invalid plugin output is detected after side effects have occurred] -> Validate immediately when the Action returns; report a normal actionable failure and permit declared default-error recovery. Side effects cannot be rolled back, matching other post-Action failures.
- [`setVars` PATCH succeeds but its response is lost] -> Keep one atomic server PATCH after complete local projection; retries remain subject to the existing idempotent deep-merge semantics. This change does not add distributed transactions.
- [Historical scalar `TaskRun.Output` conflicts with the new API contract] -> Do not migrate it or project it into `tasks.*.outputs`; new writes are guarded. Historical read behavior has no compatibility guarantee in this actively developed project.
- [Task-detail or delivery metadata disappears after removing Web string parsing] -> Add API-to-view regression coverage using object-valued PR and `core/process` outputs before deleting compatibility parsers.

## Migration Plan

1. Change runner Action/result types and the shared validator; migrate all built-in Action producers and direct consumers. Update check aggregation while keeping AgentJob execution outside the Action validator.
2. Change the runner report HTTP and Orleans types to `JsonElement?`; adapt Workflow task translation, check translation, AgentJob handling, and direct `TaskRun.Output` assignment.
3. Change task status/timeline DTOs and Web types to structured output; remove Web parsing fallbacks and verify existing JSON presentation.
4. Run runner typecheck/tests, server tests, and Web typecheck/tests. Add focused end-to-end specs for `core/process` -> `setVars`, `core/process` -> `tasks.*.outputs`, missing `setVars` paths, recovery matching, check reports, AgentJob reports, and task-detail rendering. Existing built-in Action tests must assert unchanged object fields.
5. Drain or stop active runners, deploy server + runner + Web as one release, and restart dispatch. In-flight work reported by an old runner is not migrated; rerun the affected task/stage after deployment.

Rollback requires rolling server, runner, and Web back together. There is no database schema migration to reverse: new valid outputs are already JSON objects in `TaskRun.Output`, which the old server can persist/read. Any in-flight report crossing the rollback boundary must be retried after all components use the same version.

## Open Questions

None. The output root shape, `core/process` fields, compatibility policy, and capability boundaries are fixed by the proposal and specification.
