## Context

Build-stage OpenSpec task execution currently uses `mohist/openspec-tasks` to read `tasks.json`, expand each item into a runnable task, and compose each ACP agent prompt from the selected task data. That combines task materialization with prompt construction, making prompt composition hard to reuse and increasing the chance that task JSON fields are accidentally treated as workflow template input.

`WorkItem.with` already accepts arbitrary JSON, so this change can stay local to the runner. The workflow runtime and server wire protocol do not need to know whether an ACP prompt came from a string, a structured object, or a loader. The main stakeholders are runner action authors, default workflow maintainers, and users relying on OpenSpec Build-stage tasks preserving literal task content such as `${{ prompts.xxx }}`.

The key constraint is backward compatibility: existing string prompts must behave byte-for-byte the same before the existing Mohist issue context wrapper is applied, and ACP agent tasks with no prompt must continue to use the legacy fallback prompt.

## Goals / Non-Goals

**Goals:**

- Add a runner-side prompt resolver for `mohist/acp-agent` that supports string prompts, structured object prompts, and loader-backed prompts.
- Provide a stable, LLM-friendly XML-like renderer for simple structured prompt objects.
- Add a prompt loader registry parallel to the existing action registry, with a built-in `mohist/openspec-task-prompt` loader.
- Move OpenSpec task prompt composition out of `mohist/openspec-tasks` while keeping that action responsible for reading `tasks.json` and expanding runtime tasks.
- Preserve opaque task JSON content until prompt-resolution time, avoiding template rendering of task descriptions and other task fields.
- Update the default Build-stage workflow shape so generated ACP tasks delegate prompt composition to `mohist/openspec-task-prompt` unless a caller supplies an explicit prompt override.

**Non-Goals:**

- No server, database, SSE, or workflow wire-protocol changes.
- No general step-output routing or cross-task dataflow system.
- No strict XML serialization contract or round-trip-safe structured prompt format.
- No removal or replacement of `buildFallbackPrompt` for ACP agent tasks that omit `with.prompt`.
- No expansion of `mohist/openspec-tasks` responsibilities beyond task materialization.

## Decisions

### Decision 1: Resolve prompts inside `mohist/acp-agent`

`mohist/acp-agent` will call `resolvePrompt(context.with?.prompt, promptContext)` before applying the existing Mohist issue context wrapper. If `with.prompt` is absent, the action will keep using `buildFallbackPrompt(context)`.

Rationale: prompt interpretation is an ACP-agent concern, not a workflow runtime concern. Keeping resolution in the action avoids server and runtime changes while allowing any ordinary ACP task to carry richer prompt configuration.

Alternatives considered: resolve prompts during task loading or workflow template rendering. That was rejected because it would continue to mix materialization with prompt construction and would risk interpreting JSON-loaded task content as template input too early.

### Decision 2: Introduce a small prompt core module and registry

`packages/runner/src/core/prompt.ts` will define `PromptSpec`, `StructuredPrompt`, `PromptLoaderContext`, `PromptLoaderRegistry`, `renderStructuredPrompt`, and `resolvePrompt`. A companion registry module will register built-in loaders, including `mohist/openspec-task-prompt`.

Rationale: this mirrors the existing runner action registry shape without coupling prompt loaders to runnable workflow actions. It gives tests a narrow seam for fake loaders and keeps `acp-agent.ts` focused on process execution.

Alternatives considered: reusing `ActionRegistry` for prompt loaders. That was rejected because prompt loaders return prompt data rather than workflow action results, and sharing one registry would blur lifecycle and type expectations.

### Decision 3: Use a predictable structured prompt renderer, not XML serialization

Plain object prompts and object results from loaders will be rendered into stable XML-like text. The renderer will support a small prompt-oriented shape: a root tag, optional `attrs`, string content, list content rendered as readable lines, and one practical level of nested blocks.

Rationale: LLM prompts need readable, stable structure more than XML correctness. Stable whitespace and attribute ordering make unit tests reliable and prevent prompt drift.

Alternatives considered: JSON stringify, YAML rendering, or a full XML serializer. JSON/YAML are less aligned with existing Mohist prompt style, and a full XML serializer would add unnecessary escaping and schema complexity for a non-round-trip prompt format.

### Decision 4: Keep loader results flexible but normalize at the resolver boundary

Prompt loaders may return either a string or a JSON object. `resolvePrompt` will use strings directly and pass objects through `renderStructuredPrompt`.

Rationale: some loaders may be best expressed as complete text, while OpenSpec task composition benefits from returning structured data that shares the default renderer. Normalizing in one place keeps action behavior consistent.

Alternatives considered: require every loader to return a string. That would make built-in loaders duplicate object rendering logic and make tests less focused on the shared renderer.

### Decision 5: Implement `mohist/openspec-task-prompt` as a lazy file-backed loader

The built-in loader will read the configured JSON file relative to `workDir`, locate the task array using a dotted `items` path defaulting to `tasks`, select by `taskId` against `id` or `taskId` first, fall back to `index`, and return a structured prompt containing optional `base_instructions` and `selected_task`.

Rationale: keeping task content in `tasks.json` until prompt resolution preserves it as opaque data and fixes the template-pollution failure mode. Selecting by `taskId` is more stable than index, while index remains useful for task files without IDs.

Alternatives considered: embedding selected task fields into generated runtime task `with` data. That was rejected because generated `with` values can be template-rendered by the workflow path and would reintroduce the bug this change is meant to avoid.

### Decision 6: Make `mohist/openspec-tasks` inject prompt specs only when needed

When the task template does not provide `task.with.prompt`, `mohist/openspec-tasks` will emit a runtime `mohist/acp-agent` task with `with.prompt.uses: mohist/openspec-task-prompt`, passing `file`, `items`, optional `base`, and a per-task `taskId` selector when available. If the caller already provided any prompt value, the loader preserves it unchanged.

Rationale: this keeps the loader backward compatible for existing inputs while moving default prompt composition to the new prompt loader path. Explicit prompt overrides remain authoritative.

Alternatives considered: always overwrite prompts with the built-in loader shape. That was rejected because users may intentionally supply literal prompts, structured prompts, or custom prompt loaders.

## Risks / Trade-offs

- `[Risk] Structured prompt rendering may become an implicit schema users depend on.` -> Mitigation: document and test stable whitespace for supported shapes, but keep the contract explicitly LLM-friendly rather than XML-interoperable.
- `[Risk] Loader-backed prompts can fail later than task materialization because JSON is read lazily.` -> Mitigation: produce clear errors for missing files, missing items paths, missing selectors, and missing selected tasks; cover these cases with unit tests.
- `[Risk] Relative file paths may be ambiguous.` -> Mitigation: resolve loader `file` relative to `workDir`, matching the issue requirement and existing runner file conventions.
- `[Risk] Prompt loader registry could become a second action system.` -> Mitigation: keep the interface narrow: prompt loaders receive context and return only string or object prompt data, with no workflow result semantics.
- `[Risk] Default workflow changes may break callers relying on `mohist/openspec-tasks` composing literal prompts.` -> Mitigation: preserve existing task loader keys and allow explicit `task.with.prompt` overrides to take precedence.

## Migration Plan

1. Add prompt core types, renderer, resolver, and prompt loader registry with test hooks.
2. Update `mohist/acp-agent` to resolve `with.prompt` through the new resolver while preserving string identity and missing-prompt fallback behavior.
3. Add and register `mohist/openspec-task-prompt`.
4. Update `mohist/openspec-tasks` so default generated ACP tasks carry a prompt loader spec instead of a composed literal prompt, while preserving explicit prompt overrides.
5. Update `mohist-default.workflow.yaml` Build-stage configuration to express prompt composition through `mohist/openspec-task-prompt`.
6. Add runner unit tests for prompt rendering, loader resolution, OpenSpec task prompt selection, and OpenSpec task loader output.
7. Verify with `npm run build` and `npm test` in `packages/runner`.

Rollback is local to the runner: restore `mohist/acp-agent` to string-only prompt handling, restore `mohist/openspec-tasks` literal prompt composition, and revert the default workflow prompt shape. No persisted data or server API migration is involved.

## Open Questions

- Should the structured renderer reject unsupported nested object shapes with explicit errors, or render them best-effort as readable content?
- Should `mohist/openspec-task-prompt` include all unknown selected task fields by default, or restrict output to known fields such as title, description, acceptance criteria, output, and notes?
- Should prompt loader names be namespaced only by convention, or should the registry enforce a `vendor/name` format for built-ins and custom loaders?
