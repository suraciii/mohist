## Why

Recovery task prompts that reference the triggering failure's facts — e.g. `fix-pr-checks.prompt`'s `${{ failure.output.prNumber }}` / `${{ failure.output.prUrl }}` — are reaching the agent as unrendered literals, forcing the agent to spend turns re-deriving the PR number and already causing one real incident (issue #428's integrate run). The design has long specified that the runner expands `${{ failure.* }}` in-place when constructing recovery tasks, since the triggering task's structured output lives only in runner memory; this issue closes that implementation gap so built-in recovery prompts work as authored.

## What Changes

- The runner expands `${{ failure.* }}` references in recovery tasks using the triggering task's result (the structured `output` carrying `errorCode`, `message`, and action-owned fields like `prNumber` / `prUrl`) at recovery-task construction time. After construction, the recovery task delivered to the engine no longer carries any `${{ failure.* }}` expression.
- When a recovery task references a `failure.*` path that is absent from the triggering task's output (e.g. the action returned no structured output, or a field is missing), dispatch fails with an actionable error identifying the unresolvable reference — it does NOT silently forward the literal `${{ failure.* }}` text to the agent.
- Non-recovery task template rendering is unchanged: the existing "leave embedded unresolved references as literal text" rule still holds for every other namespace, and `failure.*` continues to be rejected outside recovery-handler tasks by definition validation.
- **BREAKING** (narrow): a previously-silent misconfiguration — a recovery prompt referencing a `failure.*` path the triggering action never produces — now fails dispatch instead of sending literal text. Built-in prompts are already consistent with their triggering actions' output contracts, so no authored workflow changes.
- Remove the `${{ failure.* }}` 展开未实装 Status gap note from `design/workflow/recovery.md`; the matching design intent in `task-dispatch.md` is now realized.

## Capabilities

- `recovery-failure-context`: How `${{ failure.* }}` references behave in recovery tasks — what value each path resolves to (sourced from the triggering task's structured output), where in the runner lifecycle the expansion happens (at recovery-task construction, before the task enters the engine), and the actionable dispatch failure when a referenced path is absent. Owns the contract that recovery tasks are self-contained by the time they reach the engine and that non-recovery rendering is untouched.

## Impact

- **Runner (`packages/runner/src/runtime/recovery.ts`)** — primary surface: `tryRecovery` already holds the triggering `WorkItemResult.output`; it (or a sibling helper it calls) becomes the single authority that builds the `failure` namespace from that output and expands `${{ failure.* }}` references across the recovery task's renderable surface before returning `addTasks`.
- **Runner (`packages/runner/src/runtime/executor.ts`, `packages/runner/src/core/template.ts`)** — reused rendering primitives; the unresolved-reference diagnostic path used for `with` / `expect` is the model for the new `failure.*` dispatch error. The existing executor flow that calls `tryRecovery` is the call site.
- **Built-in prompts (`packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/fix-pr-checks.prompt`)** — consumer only; already authored against `${{ failure.output.* }}`. The `mohist/merge-github-pr` action's `pr-checks-failed` output contract (which emits `errorCode`, `prNumber`, `prUrl`, `message`) is the data source the expansion reads. No prompt or action code change.
- **Design docs (`design/workflow/recovery.md`, `design/workflow/task-dispatch.md`)** — remove the `${{ failure.* }}` Status gap note; `docs/workflow-definition.md`'s template-expression table already documents `failure.output` as recovery-only and needs no edit.
- **Tests (`packages/runner`)** — new unit/spec coverage: `${{ failure.output.* }}` expands to the triggering task's structured output; dispatch fails with an actionable error when a referenced `failure.*` path is absent; non-recovery rendering unchanged. Per `design/testing.md`: no real time, no real external deps; recovery matching already drives deterministically off `WorkItemResult.output`.
- **Server / CLI / Web / events / protocol / dependencies**: none. The triggering task's output already flows to the runner via the existing dispatch/report path; no API, DTO, or protocol change.
