## Context

The Ralph executor (`ralph-executor.ts`) reads `tasks.json` and executes tasks one at a time. The `dependsOn` field exists in the `Task` interface and is displayed in prompts, but the executor completely ignores it at runtime — `findNextPendingTask()` (line 250) sorts pending tasks by `order` only and picks the first one. There is also no validation on load: `readTasks()` (line 156) parses JSON without checking dependency integrity.

The prompts (`tasks.md`, `self-review.md`) already mention `dependsOn` but the instructions are easy for the LLM to skip — there's no explicit "analyze dependencies between every pair of tasks" step.

## Goals / Non-Goals

**Goals:**
- Make `findNextPendingTask()` respect `dependsOn` — only pick tasks whose dependencies have all passed
- Validate the dependency graph on load (reference existence, DAG, forward-dependency check)
- Strengthen the task generation prompt to produce accurate `dependsOn` fields
- Add dependency completeness to the self-review checklist

**Non-Goals:**
- Parallel task execution (still one-at-a-time)
- Rescheduling or reordering tasks at runtime beyond dependency constraints
- Changing the `Task` interface or `tasks.json` schema

## Decisions

### D1: Check dependencies inside `findNextPendingTask()` rather than pre-sorting topologically

Modify `findNextPendingTask()` to filter out tasks whose `dependsOn` entries haven't all passed, then sort remaining candidates by `order`. This keeps the change localized to one function and preserves the existing `order`-based tie-breaking behavior.

**Alternatives considered:**
- *Topological sort on load* — would require restructuring the tasks array and complicates resume scenarios where some tasks are already passed. Overkill when order-based tie-breaking already works for most cases.
- *Separate ready-queue data structure* — adds state management complexity for no measurable benefit in a sequential executor.

### D2: Add `validateTaskDependencies()` as a standalone function, called from `runRalphLoop()`

A pure function that takes the task list and returns either `ok` or a list of validation errors. Called once at the top of `runRalphLoop()`, after `readTasks()` but before the while-loop. On failure, return immediately with a failed `RalphLoopResult` and log the errors.

Three checks:
1. **Reference existence**: every `dependsOn` ID exists in the task list
2. **DAG / no cycles**: DFS cycle detection — since we also enforce forward-dependency (check 3), cycles are impossible if check 3 passes, but we validate both for defense-in-depth
3. **Forward dependency**: every `dependsOn` entry references a task with a strictly lower `order` value

**Alternatives considered:**
- *JSON Schema validation* — would catch missing fields but not semantic errors like cycles or forward references. Would need a separate validation pass anyway.
- *Validate only in tests* — the spec requires runtime validation before execution.

### D3: Deadlock detection in the while-loop

When `findNextPendingTask()` returns `null` but there are still pending (non-passed) tasks, this means all remaining tasks are blocked by unmet dependencies — a deadlock. Log a warning and return a failed result with `pauseReason` explaining the deadlock.

### D4: Strengthen prompt with explicit dependency analysis step

Add a "Dependency Analysis" section to `tasks.md` that instructs the agent to:
1. List every pair of tasks and determine if one depends on the other
2. Fill `dependsOn` for every non-first task (at minimum, depend on the task that produces its inputs)
3. Provide a second example showing a multi-dependency chain

The self-review prompt (`self-review.md`) gets an explicit checklist item: "Every non-first task has at least one `dependsOn` entry". The `planner-self-review.yaml` gets a new criterion under `feasibility` for dependency completeness.

## Risks / Trade-offs

- **[Existing tasks.json without dependsOn]** → Backward compatible: `dependsOn: []` or `undefined` means "no dependencies", so existing tasks execute exactly as before (sorted by `order`).
- **[Agent still generates empty dependsOn despite prompt changes]** → Runtime validation won't catch this (empty `dependsOn` is valid). The self-review checklist and stronger prompt are the mitigation. The runtime enforcement ensures that *if* dependencies are declared, they are respected.
- **[Deadlock from incorrect dependsOn]** → The deadlock detection in D3 will catch this and report it clearly, rather than silently looping forever.

## Migration Plan

No migration needed. The change is backward compatible — tasks with empty or missing `dependsOn` behave identically to the current behavior. No changes to `tasks.json` schema or API contracts.

## Open Questions

None.
