## Context

The [proposal](./proposal.md) and [specifications](./specs) require Workflow task completion policy to leave Action Input and become a task-level contract. Today three ownership defects make task success and execution configuration unreliable:

1. **`expect` is Action Input.** `WorkflowYamlSerializer.ToTask` (`WorkflowYamlSerializer.cs:81`) never reads top-level `expect`; it reads `with.expect` only. The built-in `mohist/github-pr` profile declares `expect` at task level, but the parser silently drops it. Only `mohist/acp-agent` interprets `with.expect`, and it does so inside the Action (`acp-agent.ts:37`).
2. **Hidden variable injection.** `WorkflowItemTranslator.ResolveWith` (`WorkflowItemTranslator.cs:242`) synthesizes `with.agent` from `vars.agent` when the task omits it. `TaskWithExpander.Expand` (`TaskWithExpander.cs:40`) deep-merges same-named variable objects into `with`. Configuration reaches Actions through paths the task never declared.
3. **Legacy execution-backend keys.** `variables.agent` in built-in profiles carries `type: opencode`; the Web `IssueModelSelector` and `MohistIssueWorkflowProfileBase` add the same key. Model validation rejects IDs with additional `/` segments (`IssueModelMetadata.cs:50`).

The affected path crosses both planes:

```text
TaskDefinition (YAML / persisted JSON)
  -> TaskRun
  -> WorkflowTaskWork / WorkItem
  -> WorkDispatch / runner poll response
  -> RenderedWorkItem / executor / Action
  -> expectation evaluation / output projection
  -> recovery matching
  -> addTasks / RuntimeTaskInput
  -> TaskRun
```

Architecture constraints that shape the design:

- Completion evaluation needs filesystem access (files, markers), which lives only in the Runner workspace. The Server is state authority, not execution environment ([`design/architecture.md`](../../../design/architecture.md): "workspace prep/clean → Runner").
- Runner reports facts; Workflow interprets them. The completion evaluator belongs in the Runner's task executor, not in the Action and not in the Server grain.
- Variables, Prompts, and runtime context are independent namespaces. Only explicit `${{ vars.* }}` bindings should affect Action Input ([`design/workflow/variables.md`](../../../design/workflow/variables.md), [`design/workflow/task-dispatch.md`](../../../design/workflow/task-dispatch.md)).

Stakeholders: workflow maintainers (profile contract), runner maintainers (executor, Action registry), and every operator whose custom profile or in-flight run depends on the current task shape.

## Goals / Non-Goals

**Goals:**

- Add `expect` as a task-level completion contract beside `with`, `artifacts`, `setVars`, and `recovery`; propagate it through every boundary that carries task definition data.
- Move file/marker/`failIf`/`_output` evaluation from the `mohist/acp-agent` Action into the Workflow task executor.
- Support `path: _output` against the turn's final assistant text, carried as a private Action-result fact that never becomes Action Output.
- Project `{ promise }` output for `mohist/opencode` tasks when a promise marker matches, without imposing a generic output schema on other Actions.
- Remove implicit variable injection and same-key deep-merge into Action Input.
- Preserve JSON types for whole-value variable expansion.
- Reject legacy `with.expect` and `with.agent` shapes for inline-agent tasks with actionable errors.
- Migrate both built-in profiles and all generated agent tasks to the canonical contract.

**Non-Goals:**

- Implement the native OpenCode process, Session, or SDK behavior. That is the sibling runtime change in epic 46.
- Define a `mohist/agent` Action or reference predefined Mohist Agents from Workflow tasks.
- Impose a unified business output schema across all Actions.
- Provide a compatibility path for `with.expect`.

## Decisions

### D1: Carry `expect` as an opaque field alongside `with`

Add `Expect` to every shape that carries task definition data, using the same representation as `With`: `Dictionary<string, JsonElement?>?` on the server, `JsonObject | null` on the runner. The `expect` structure (`files`, `markers` with `path`/`oneOf`/`contains`/`failIf`) is interpreted at exactly two evaluation boundaries:

- **Server**: `TaskRunExtensions.ExtractRequiredFiles` (`TaskRun.cs:61`) reads it for evidence projection. Currently it reads `WithInput["expect"]`; it reads `Expect` instead.
- **Runner**: the new completion evaluator reads it from the dispatch envelope.

| Boundary | Shape |
|---|---|
| `TaskDefinition` | add `Expect` parameter |
| `TaskRun` | add `Expect` (definition-owned, like `Artifacts`/`SetVars`/`Recovery`) |
| `WorkflowTaskWork` | add `Expect` |
| `WorkItem` (task variant) | add `Expect` (`[property: Id(11)]`) |
| `TaskDefinitionSurrogate` | add `Expect` |
| `RuntimeTaskInput` | add `Expect` |
| `AddTasksBatchItem` | add `Expect` |
| `FeedbackTaskConfig` → replace with `TaskDefinition` | the feedback task gains the full canonical shape |
| `WorkDispatch` | add `Expect` (`[property: Id(18)]`, JSON string) |
| `WorkDispatchResponse` (runner TS) | add `expect?: string \| null` |
| `RenderedWorkItem` (runner TS) | add `expect?: JsonObject \| null` |
| `AddTaskInput` (runner TS) | add `expect?: JsonObject \| null` |

`TaskRun.ToDefinition()` includes `Expect` in the definition projection so manual retry and self-retry reconstruct it. `recovery.ts:tryRecovery` copies `work.expect` into the self-retry alongside `work.with`, `work.recovery`, and `work.recoveryRemaining`.

Alternatives considered:

- **Typed `TaskExpectation` record.** Rejected because it would cross Orleans surrogates, JSON persistence, and the runner TS mirror, multiplying surface area for a structure that is parsed in exactly two places and may still need to support arbitrary marker values.
- **Keep `expect` inside `With` and strip at dispatch.** Rejected because it preserves the coupling the proposal breaks: the Action would still see (or risk seeing) the completion contract, and YAML round-trip would need special-casing.

### D2: Promote `FeedbackTaskConfig` to `TaskDefinition`

`FeedbackTaskConfig` (`WorkflowDefinition.cs:47`) currently carries a reduced shape (`Id`, `Title`, `Uses`, `With`). Replace it with `TaskDefinition` in `ApprovalFeedbackConfig`. `WorkflowRun.ResolveFeedbackTask` (`WorkflowRun.Approval.cs:82`) already converts to `TaskDefinition`; after promotion it only adds the default `session` binding. The YAML serializer's `ToFeedbackTask` produces a `TaskDefinition` directly.

This makes feedback tasks capable of carrying `expect`, `artifacts`, `setVars`, and `recovery`, satisfying the spec requirement that all task origins use the same declaration. The built-in feedback task adds `options: ${{ vars.agent }}` to its `with`.

Alternatives considered:

- **Add the missing fields to `FeedbackTaskConfig`.** Rejected because it duplicates `TaskDefinition` under a different name for no semantic reason.

### D3: Workflow task executor owns completion evaluation

The current `verifyExpectations` (`expectations.ts:23`) reads `context.with.expect` and is called only from `acp-agent.ts:37`. Move it to a new Workflow-owned completion step in `WorkExecutor.executeOne` (`executor.ts:100`), positioned after Action normalization and before recovery:

```text
render with → render expect → Action → normalize status
  → evaluateCompletion(expandedExpect, actionResult, workDir)
  → projectOutput(uses, completionResult)
  → tryRecovery(work, finalResult)
  → (if not recovered) branch-stability → worktree → artifacts → output-capture → setVars
```

The evaluator reads the expanded `expect` (from the dispatch envelope, re-rendered by the runner) — not from `ActionContext.with`. It returns a structured result: satisfied/unsatisfied, matched marker values, failure detail. The Action and `ActionContext` never receive `expect`.

The evaluator reuses the existing file-existence, marker-matching, and `failIf` logic from `expectations.ts`, but with two corrections:

- **Marker precedence**: for file-backed markers, the matched value is the first present value in `oneOf` declaration order. For `_output`, the matched value is the last accepted occurrence in the text.
- **`_output` parsing**: `parseLastMarker` (`expectations.ts:95`) currently recognizes only lowercase `done|unfinished`. Generalize it to extract the last `<promise>VALUE</promise>` occurrence and match against the marker's configured `oneOf`/`contains` values.

Alternatives considered:

- **Server-side completion evaluation.** Rejected because the evaluator needs filesystem access (expect.files, marker file contents), which is available only in the Runner workspace.
- **Pass `expect` through `ActionContext` and let the Action call the evaluator.** Rejected because the spec requires Actions and runtimes to never receive or interpret the completion contract.
- **Keep the evaluator in the Action but remove the implicit repair turn.** Rejected because it keeps completion policy coupled to a specific Action, making it unavailable to future Actions.

### D4: Private turn-fact channel for `_output`

Extend the runner-internal `ActionResult` (`types.ts:228`) with an optional private field:

```ts
export interface ActionResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
  // Runner-internal; never serialized into WorkItemResult.output or TaskRun.Output.
  turnFact?: { finalAssistantText?: string } | null
}
```

The `mohist/opencode` bridge handler populates `turnFact.finalAssistantText` from the completed turn. The executor reads it for `_output` marker evaluation. It is stripped before the result is normalized into `WorkItemResult` — it never reaches the Server, `TaskRun.Output`, recovery matching, `setVars` projections, captured outputs, or artifacts.

The `WorkItemResult` type (`types.ts:177`) does not gain this field. The boundary between `ActionResult` (internal) and `WorkItemResult` (wire) is where the fact is dropped.

Alternatives considered:

- **Include the text in Action Output and strip it after evaluation.** Rejected because recovery matching, `setVars`, and persisted output would all see the text before stripping, violating the privacy requirement.
- **Write the text to a temp file and read it back.** Rejected because it introduces workspace state, cleanup concerns, and a file that is genuinely not a file.
- **Pass the text through a side channel on `ActionContext`.** Rejected because it gives every Action access to the channel, not just the one that produced the text.

### D5: Action-specific output projection

After completion evaluation, the executor projects the task's public output based on the selected Action:

- **`mohist/opencode`**: discard the handler's raw output. If a promise marker matched, set output to `{ "promise": "<value>" }`. Otherwise set output to `null`.
- **All other Actions**: preserve the handler's output unchanged.

The projection happens after completion evaluation (so the matched marker value is available) but before recovery matching (so `when: promise=FAIL` can match the projected output). For `mohist/opencode`, this replaces the current `acp-agent.ts` behavior of serializing transcript text, model, diagnostics, and expectation details into output.

Completion diagnostics (missing files, unsatisfied markers, failIf matches) go into the result's `message` field, not into the JSON `output`.

Alternatives considered:

- **Generic projection for all Actions.** Rejected because the spec requires Action-specific projection only where the Action contract defines it; other Actions own their output.
- **Let the Action synthesize `{ promise }`.** Rejected because the spec requires the Action and runtime to never evaluate `expect` or synthesize this output.

### D6: Action-aware legacy validation

During profile loading (`WorkflowYamlSerializer.ToTask`) and dispatch, reject legacy inline-agent shapes:

| `uses` | `with.expect` | `with.agent` |
|---|---|---|
| `mohist/opencode` | reject → move to task-level `expect` | reject → bind `options: ${{ vars.agent }}` |
| `mohist/acp-agent` (legacy persisted) | reject | reject |
| any other Action | accept as Action-owned input | accept as Action-owned input |

The validation targets known inline-agent `uses` identifiers. It does not require a registry of Action input contracts — only the two agent Action names need the legacy rule. `mohist/github-pr-status` declaring `with.expect: merged` remains valid because that Action owns the field.

The error message identifies the task id and the invalid field, and directs the author to the canonical replacement. For dispatch-time enforcement (persisted/in-flight legacy tasks that bypassed profile ingestion), the translator or executor fails the dispatch with the same actionable error.

Alternatives considered:

- **Generic rejection of `with.expect` and `with.agent` for all tasks.** Rejected because it bans fields genuinely owned by other Actions (`mohist/github-pr-status`, `mohist/marker`, `mohist/merge-ready`).
- **Full Action input-contract registry.** Rejected as scope creep for two legacy field names on two Action identifiers.

### D7: Remove implicit variable injection and deep-merge

Three behaviors change in the server-side dispatch rendering:

1. **`TaskWithExpander.Expand`** (`TaskWithExpander.cs:40`): remove the deep-merge branch (lines 40–47). Keep only whole-value `${{ vars.* }}` resolution. Apply the same function to both `with` and `expect`.
2. **`WorkflowItemTranslator.ResolveWith`** (`WorkflowItemTranslator.cs:242`): remove the `with.agent` synthesis (lines 242–249). The rendered `with` is exactly the task's declared `with` with whole-value variable references resolved.
3. **`WorkflowProfileManager.ExpandTaskWith`**: rename or keep as the single entry point; it now does whole-value resolution only.

The runner's `renderTemplate` (`template.ts`) already preserves JSON types for whole-value references (line 44) and leaves embedded unresolved references literal (line 60). No runner-side change is needed for type preservation; the runner already does not inject or deep-merge.

The `LITERAL_FIELD_PATHS` set (`template.ts:13`) currently protects `expect.markers.*.contains` when `expect` is inside `with`. After separation, `expect` is rendered as an independent object; the literal path becomes `markers.*.contains` relative to the `expect` root. Add `markers.*.contains` and `markers.*.oneOf.*` to the literal set when rendering `expect`.

Alternatives considered:

- **Opt-in deep-merge.** Rejected because the spec mandates its removal.
- **Runner-side variable injection.** Rejected because the runner already receives one composed `variables` object and should not synthesize Action fields from it.

### D8: Model ID validation accepts additional slashes

Change the regex in `IssueModelMetadata.cs:50` from `^[^/\s]+/[^/\s]+$` (exactly one slash) to split at the first slash only: non-empty provider before the first `/`, non-empty model ID (which may contain additional `/`) after it. The same validation applies to both top-level and per-stage model selectors.

The Web `IssueModelSelector` and `MohistIssueWorkflowProfileBase` stop adding `type: opencode` to `vars.agent`. Existing persisted `vars.agent` objects with legacy keys are not rewritten; the `mohist/opencode` options contract ignores non-`model`/`variant` keys with a diagnostic.

Alternatives considered:

- **Full model ID normalization (strip extra slashes).** Rejected because model IDs legitimately contain slashes (e.g., `openrouter/vendor/family/model`); the ID must reach the provider unchanged.

### D9: Built-in profile migration

Both built-in profiles (`mohist-local`, `mohist/github-pr`) receive the same mechanical transformation:

| Before | After |
|---|---|
| `variables.agent.type: opencode` | `variables.agent: {}` (or omit; model/variant come from Issue selection) |
| `uses: mohist/acp-agent` | `uses: mohist/opencode` |
| `agent: ${{ vars.agent }}` | `options: ${{ vars.agent }}` |
| `with.expect: { ... }` | top-level `expect: { ... }` |
| `apply-feedback` task: no model binding | `apply-feedback` task: `options: ${{ vars.agent }}` |

All nested recovery tasks and self-retry tasks receive the same treatment. The `openspecTasksAction` default `uses` changes from `mohist/acp-agent` to `mohist/opencode`, and `mergeTaskWith` (`openspec.ts:407`) propagates `expect` from the task template if present.

Stage order, approval points, Action-owned inputs (e.g., `mohist/github-pr-status`'s `with.expect: merged`), artifacts, `setVars`, recovery budgets, handler ordering, and delivery behavior remain unchanged.

Previously discarded top-level `expect` declarations (e.g., `proposal`, `design`, `tasks` in `mohist/github-pr`) become effective after migration. This is an intentional behavior correction called out in the proposal.

### D10: `mohist/opencode` bridge handler

This change defines the `mohist/opencode` Action contract (input/output shapes, validation rules, promise projection) but does not implement the native OpenCode SDK runtime (Non-Goal). Register a bridge handler in the runner that:

1. Accepts the new input shape (`prompt`, `session`, `options`).
2. Validates the input (non-empty prompt, `options.model`/`options.variant` are strings if present, first-slash model parsing).
3. Delegates turn execution to the existing ACP runtime underneath (the ACP process still exists).
4. Returns the raw result plus the private `turnFact`.
5. Does NOT evaluate `expect` (the executor does that now).
6. Does NOT append variant as a slash segment to the model ID (the first-slash split replaces the old `model/variant` composition in `model-resolution.ts`).

The executor's output projection (D5) strips the bridge handler's raw output to `null | { promise }`. The bridge handler is a temporary execution path; the sibling runtime issue replaces it with the native `@opencode-ai/sdk/v2` implementation and removes ACP entirely.

Alternatives considered:

- **Do not register a handler; profiles reference a non-existent Action until the runtime issue lands.** Rejected because it makes the workflow non-functional between the two issues and prevents testing the contract end-to-end.
- **Keep profiles on `mohist/acp-agent` until the runtime issue.** Rejected because it preserves the legacy `with.expect`/`with.agent` shape that this issue rejects, and the spec requires built-in profiles to use `mohist/opencode`.

## Risks / Trade-offs

- `[Previously discarded top-level expect becomes effective]` -> Migration activates expect declarations that were silently dropped. The `proposal`/`design`/`tasks` tasks in `mohist/github-pr` will now fail if the expected file is missing. This is an intentional correction; verify each built-in task's expected files against its prompt output before merging.
- `[Bridge handler masks runtime gaps]` -> The ACP-backed bridge lets workflows run, but ACP-specific behavior (liveness probes, compaction, implicit repair turns) may diverge from the native OpenCode contract. The bridge removes the implicit repair turn and expectation evaluation; remaining ACP execution behavior is accepted as temporary.
- [`mohist/opencode` and runtime issue must ship together] -> The built-in profiles reference `mohist/opencode`; the ACP handler is still needed as the bridge. If this change ships without the runtime change, agent tasks depend on the bridge. If the runtime change ships without this change, it has no canonical profiles to run. Deploy as a coordinated set within epic 46.
- `[Custom profiles with with.expect break at load]` -> Any custom profile using `with.expect` or `with.agent` for agent tasks will fail profile loading with an actionable error. There is no compatibility path; operators must update the profile and rerun the affected stage. This is the intended breaking change.
- [`_output` marker precedence differs from file markers] -> File markers use first-in-declaration-order; `_output` uses last-occurrence-in-text. This asymmetry is intentional (file content is checked for presence; turn text reflects the final answer) but could surprise authors. Document it in `design/workflow/actions.md`.
- `[Dispatch envelope grows]` -> Adding `expect` to `WorkDispatch` / `WorkDispatchResponse` increases the wire payload for every task. The `expect` field is typically small (a few file paths and marker values); the overhead is negligible compared to `variables` and `prompts`.
- `[Legacy persisted runs with with.expect in TaskRun]` -> In-flight runs persisted before this change carry `expect` inside `WithInput`. Dispatch-time enforcement rejects these with a migration error. The run requires the profile to be updated and the stage rerun.

## Migration Plan

Single repository, active development, no version compatibility required (AGENTS.md). Deploy server and runner as one coordinated change.

1. **Server domain layer**: add `Expect` to `TaskDefinition`, `TaskRun`, `WorkflowTaskWork`, `WorkItem`, surrogates, `RuntimeTaskInput`, `AddTasksBatchItem`. Replace `FeedbackTaskConfig` with `TaskDefinition`. Update `TaskRunExtensions.ExtractRequiredFiles` to read from `Expect`. Update `TaskRun.ToDefinition()` to include `Expect`.
2. **Server YAML and validation**: update `WorkflowYamlSerializer.ToTask` to read top-level `expect`. Add inline-agent legacy validation (D6). Update `ToTaskMap` to emit `expect`. Remove deep-merge from `TaskWithExpander.Expand`. Remove `with.agent` synthesis from `WorkflowItemTranslator.ResolveWith`. Expand `expect` at dispatch time.
3. **Server dispatch contract**: add `Expect` to `WorkDispatch`. Update `WorkflowItemTranslator.BuildTaskDispatchAsync` to expand and serialize `expect`.
4. **Server model validation**: update `IssueModelMetadata` regex. Stop adding `type: opencode` in `MohistIssueWorkflowProfileBase`.
5. **Runner types**: add `expect` to `WorkDispatchResponse`, `RenderedWorkItem`, `AddTaskInput`. Add `turnFact` to `ActionResult`. Update `connection.ts:toWorkItem` and `report()` to carry `expect`.
6. **Runner executor**: add completion evaluation step (D3), output projection (D5), `_output` private fact (D4). Update executor step ordering. Update `recovery.ts:tryRecovery` to copy `expect` into self-retry.
7. **Runner Actions**: register `mohist/opencode` bridge handler (D10). Update `openspec.ts` default `uses` and `mergeTaskWith` to propagate `expect`. Remove `expect` evaluation and implicit repair from the ACP handler path.
8. **Runner template**: update `LITERAL_FIELD_PATHS` for standalone `expect` rendering. Add `expect` rendering in `executeOne`.
9. **Built-in profiles**: migrate both YAML files (D9).
10. **Web**: remove `type: 'opencode'` from `IssueModelSelector` variable patches.
11. **Design docs**: update `design/workflow/actions.md`, `design/workflow/task-dispatch.md`, and `design/workflow/builtin-workflows.md` to reflect the migrated contract.

Rollback: drain workflow dispatch, roll back server and runner together. Existing in-flight runs persisted with the old shape require the stage to be rerun after rollback. No database schema migration runs; `Expect` is additive state within the existing `WorkflowRun` JSON document.

## Open Questions

- **Marker text in `contains` vs `oneOf` during `_output` evaluation**: the generalized `_output` parser extracts `<promise>VALUE</promise>` tags. A `contains`-form marker (without `oneOf`) supplies a single literal search string. Should `_output` support `contains` markers that are not `<promise>` tags, or should `_output` require `oneOf`? Current built-in usage only has `oneOf`. Lean toward: `_output` supports both forms, matching any accepted value as a substring, with last-occurrence precedence for `oneOf` and simple presence for `contains`.
- **Completion diagnostics granularity**: the spec requires failure detail to identify the missing path or unsatisfied marker. The exact format of this detail (structured JSON in `message` vs human-readable text) is an implementation detail. Lean toward human-readable text in `message`, matching the current `expectations.ts:buildMessage` pattern, since the detail is consumed by task failure display, not by programmatic recovery matching.
