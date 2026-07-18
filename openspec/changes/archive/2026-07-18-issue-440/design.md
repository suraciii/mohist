## Context

`fix-pr-checks.prompt` (and any future recovery prompt) is authored against `${{ failure.output.* }}` references — the prompt body literally says `failed for PR #${{ failure.output.prNumber }}`. The runner constructs recovery tasks in `packages/runner/src/runtime/recovery.ts:tryRecovery` and hands them back to the engine via `addTasks`. Today that construction clones the handler's declared tasks verbatim: the prompt field reaches the engine as `${{ prompts.fix-pr-checks }}`, the body is fetched at execution time, and `${{ failure.* }}` inside the body is never substituted — the agent receives literal text. The triggering task's structured `output` (carrying `errorCode`, `prNumber`, `prUrl`, `message`) is in `tryRecovery`'s hands via `WorkItemResult.output` and is currently used only for `when` matching, then discarded.

The design (`design/workflow/recovery.md`, `task-dispatch.md`) already fixes the locus: "runner 构造恢复任务时就地展开", "插入引擎的恢复任务不再含该表达式". This document is the implementation plan; the contract is `proposal.md` + `specs/recovery-failure-context/spec.md`.

Relevant code today:
- `packages/runner/src/runtime/recovery.ts:15` — `tryRecovery(work, result)` returns `WorkItemResult | null`; `readAddTasks` clones handler tasks with no template work.
- `packages/runner/src/runtime/executor.ts:166` — the single call site, already in possession of `variables` from line 138.
- `packages/runner/src/core/template.ts` — `renderTemplate`, `findTemplateReferences`, `wholeStringUnresolvedReferences`; the renderer's "embedded unresolved → leave literal" rule is in `renderString` (lines 70–80). None of these primitives do namespace-scoped expansion today.
- `packages/runner/src/core/prompt.ts:resolvePrompt` — string specs return verbatim; no second-pass rendering of bodies.
- `packages/runner/tests/executor-recovery.spec.ts` — existing recovery specs; the natural extension point for the new behavior.

## Goals / Non-Goals

**Goals:**
- Expand `${{ failure.* }}` references in recovery handler tasks (including refs inside `${{ prompts.<key> }}` bodies) using the triggering task's structured `output`, so the agent never receives literal `${{ failure.* }}` text.
- Fail with an actionable, named diagnostic when a `failure.*` path is unresolvable (whether whole-string or embedded) — no silent literal passthrough.
- Preserve every other template-rendering rule unchanged (dispatch-time `vars.*` resolution, embedded-literal tolerance for non-`failure` namespaces, definition-time rejection of `failure.*` outside recovery handlers).

**Non-Goals:**
- No new top-level `failure.*` shortcuts (e.g. `failure.errorCode`). Only `failure.output` and its sub-paths, matching `task-dispatch.md` and `docs/workflow-definition.md`.
- No engine, DTO, or protocol change. The triggering task's output already reaches the runner; nothing new flows back to the server.
- No re-design of the recovery budget / `retrySelf` / `recoveryRemaining` plumbing.
- No change to the rule that `${{ prompts.<key> }}` bodies are re-read at execution time for *non-recovery* tasks.

## Decisions

### Decision 1: Expand at recovery construction in `tryRecovery`, not via a second executor pass

`tryRecovery` is the one place that holds both the triggering `WorkItemResult.output` and the handler's declared tasks. Expanding there keeps the failure context local to where it is known and produces an `addTasks` payload whose prompt body already contains the real values, matching the design's "插入引擎的恢复任务不再含该表达式".

Signature change: `tryRecovery(work, result)` → `tryRecovery(work, result, variables)`. `variables` is already computed in `executor.executeOne` (line 138) before the `tryRecovery` call (line 166); it carries the `prompts` registry needed to resolve `${{ prompts.<key> }}` bodies.

**Alternatives considered:**
- *Thread a `failure` namespace through dispatch to engine → runner.* Requires a new field on `addTasks` / `TaskRun` / `WorkDispatchResponse`, a server-side DTO change, and a second-pass renderer in the opencode action (since `resolvePrompt` returns string bodies verbatim). Rejected: bigger blast radius, spreads the change across runner + server + per-action code, and contradicts "插入引擎的恢复任务不再含该表达式".
- *Add a general "render prompt body against dispatch variables" second pass for all tasks.* Rejected: changes the documented contract that prompt bodies are passed through verbatim, and is unnecessary for non-recovery tasks.

### Decision 2: Targeted namespace-scoped expansion (only `${{ failure.* }}`)

Introduce a new primitive — `expandFailureReferences(value, failureContext)` — that walks a JSON value and substitutes `${{ failure.* }}` references only. Other `${{ }}` references (`${{ vars.agent }}`, `${{ workspace.branch }}`, `${{ stage.name }}`, etc.) are left byte-for-byte intact, so they continue to follow the dispatch-time rules from `task-dispatch.md` and the late-expansion contract from issue #436 (runtime-task-late-expansion spec).

The primitive handles both forms per `specs/recovery-failure-context/spec.md`:
- Whole-string `${{ failure.output }}` or `${{ failure.output.X }}` → preserve JSON type (object / array / number / boolean).
- Embedded `... ${{ failure.output.X }} ...` → string substitution.
- Unresolvable `${{ failure.* }}` path → throw with a diagnostic naming the path; the catch site translates that into the `WorkItemResult` failure message.

The existing `renderTemplate` cannot be reused directly because it would also expand `vars.*`, `workspace.*`, etc. Running it with a variables bag that only contains `failure` does not work either — it would throw on the whole-string `${{ prompts.fix-pr-checks }}` and `${{ vars.agent }}` references that we must preserve.

**Alternatives considered:**
- *Add a `scopes` parameter to `renderTemplate`.* Tempting for reuse, but `renderTemplate`'s "embedded unresolved → leave literal" rule is exactly what we must NOT apply to `failure.*`. Keeping the new primitive separate preserves the existing renderer's contract and avoids a scoped-behavior flag that would be load-bearing only here.

### Decision 3: Pre-render `${{ prompts.<key> }}` bodies inline for recovery tasks

Because `failure.*` references live inside prompt bodies (loaded by key at execution time for ordinary tasks), the failure-context pass must see the body. The pass therefore:

1. Detects a whole-string `${{ prompts.<key> }}$` reference in any field of the handler task's `with` (and `expect`).
2. Resolves the body from `variables.prompts[key]` (the same source the executor's `renderTemplate` reads when substituting `${{ prompts.* }}` for ordinary tasks).
3. Applies `expandFailureReferences` to the body.
4. Inlines the expanded body as a literal string into the field, replacing the `${{ prompts.<key> }}` reference.

The recovery task reaching the engine therefore has `with.prompt` set to a literal string (the expanded body), not a `${{ prompts.<key> }}` reference. When the executor later runs `renderTemplate(work.with, variables)` and `resolvePrompt` on the recovery task, the literal string passes through verbatim — the agent gets the expanded text.

**Trade-off accepted:** recovery task prompts are *not* re-read at execution time. If a project edits `fix-pr-checks.prompt` between recovery construction and the recovery task's execution, the recovery task uses the snapshot taken at construction. This is acceptable because (a) recovery is short-lived (single attempt within the same dispatch report), (b) redeliveries of the *same* recovery task attempt are idempotent on the inlined body, and (c) a fresh recovery attempt on a *new* triggering-task dispatch re-runs `tryRecovery` and picks up the latest body. The benefit — single-location change, no DTO plumbing, contract-matching wire format — outweighs the cost.

Fields that contain `${{ failure.* }}$` directly (not via a `${{ prompts.<key> }}$` reference) are handled by the same `expandFailureReferences` walk; no body resolution is needed for them.

**Alternatives considered:**
- *Leave `${{ prompts.<key> }}` as a reference and add a second template pass inside the opencode action for recovery tasks.* Rejected: requires the action to know it is a recovery task (coupling), requires threading the failure context through dispatch (Decision 1 alternative), and breaks the "prompts.<key> resolved once" rule of `resolvePrompt`.
- *Eagerly resolve `${{ prompts.<key> }}` for ALL fields, not just whole-string references.* Rejected: the documented pattern is `${{ prompts.<key> }}` as a standalone reference; embedded `${{ prompts.<key> }}` inside larger strings is not a documented case. The pass handles whole-string refs and falls back to leaving embedded `${{ prompts.* }}` text alone for the renderer's normal handling.

### Decision 4: Unresolved `failure.*` fails the whole recovery construction

When `expandFailureReferences` throws on an unresolvable `${{ failure.* }}$` path, `tryRecovery` catches it and returns a *failure* `WorkItemResult` (status `failed`, message: the diagnostic naming the path and the recovery task id) instead of a completed-with-addTasks result. The triggering task is therefore marked failed by the engine and surfaces in the workflow timeline with an actionable message.

This is the strictest choice and the one the issue demands ("dispatch 以可操作错误失败，而不是静默下发字面量"). It guarantees no `${{ failure.* }}$` text reaches the agent under any circumstance.

**Alternatives considered:**
- *Skip only the offending recovery task and return the rest of the handler's tasks.* Rejected: a partial recovery sequence (e.g. `fix-pr-checks` dropped but `push` retained) cannot succeed and would fail in confusing ways downstream. Failing fast at construction is clearer.
- *Return a per-task "construction-failed" marker for the engine to render.* Rejected: requires a new task-state value in the engine and a DTO change.

### Decision 5: Apply the pass to every template-bearing field of the handler task, including `expect`

The handler task's `with`, `expect`, and any nested fields are all in scope. `readAddTasks` already preserves `with`, `expect`, `artifacts`, `setVars`, `recovery`, and `recoveryRemaining` on `AddTaskInput`; the failure-context pass walks `with` and `expect` (the template-bearing fields per `definition.md`'s validation rules). `artifacts`, `setVars`, and `recovery` do not legally contain `${{ failure.* }}$` per the validation table (`failure.*` only allowed in recovery handler task templates) and are passed through untouched.

### Decision 6: `retrySelf` clone is excluded from the pass

The retry-self clone (the triggering task re-queued with `recoveryRemaining - 1`) is the *original* task, not a recovery handler task. Per `definition.md`, `failure.*` is not legal there, and per `specs/recovery-failure-context/spec.md` requirement 4 it must not resolve. The pass is applied only to `handler.tasks`, never to the retry-self clone.

## Risks / Trade-offs

- **[Recovery prompts snapshot at construction]** → If a project edits a recovery prompt body between recovery construction and the recovery task's execution, the snapshot is used. *Mitigation*: recovery attempts are short-lived within a single dispatch report; new triggering-task dispatches re-run `tryRecovery` and re-read the body. Documented as a known trade-off in `design/workflow/recovery.md` Status section if it ever matters; no current built-in prompt depends on mid-flight edits.
- **[Breaks previously-silent misconfigurations]** → A project workflow whose recovery prompt references a `failure.*` path the triggering action never produces now fails dispatch instead of sending literal text. *Mitigation*: this is the intended behavior change; the diagnostic names the bad path and task so authors can fix the prompt. Built-in prompts are already consistent with their triggering actions' output contracts (verified against `mohist/merge-github-pr`'s `pr-checks-failed` output shape). Called out as BREAKING (narrow) in `proposal.md`.
- **[Signature change to `tryRecovery`]** → Existing direct callers in `packages/runner/tests/executor-recovery.spec.ts` need a `variables` argument. *Mitigation*: tests construct `variables` already; the helper signature is internal and not exported outside the runner.
- **[Targeted expansion duplicates some template logic]** → `expandFailureReferences` reimplements reference matching narrowly. *Mitigation*: reuses `REFERENCE_PATTERN` and `resolvePath`-style logic from `core/template.ts`; the new primitive is ~50 lines and tested in isolation.

## Migration Plan

- **Deploy**: runner-only change. Ship via the normal `mo update` runner-version bump (per `AGENTS.md`). No server-side migration, no DB change, no DTO/protocol version bump.
- **Order**: land `recovery.ts` + `core/template.ts` helper + `executor.ts` signature change in one commit; tests in the same commit (`packages/runner/tests/executor-recovery.spec.ts` extension + a focused unit test for `expandFailureReferences`).
- **Doc updates in the same change**: remove the `${{ failure.* }}` 展开未实装 bullet from `design/workflow/recovery.md` Status section. `design/workflow/task-dispatch.md` needs no edit — its prose already describes the target behavior. `docs/workflow-definition.md` already documents `failure.output` as recovery-only.
- **Rollback**: revert the commit; the runner resumes the prior (literal-passthrough) behavior. No persisted state depends on the new behavior — recovery tasks constructed under the new code reach the engine already-expanded, so a rolled-back runner simply stops expanding going forward.

## Open Questions

- **Diagnostic format**: should the unresolved-`failure.*` diagnostic cite just the path (`failure.output.prNumber`), or the path plus the recovery task id (`recovery task 'recover:fix-pr-checks' references unresolved failure.output.prNumber`)? Plan: the latter, mirroring `formatUnresolvedError` in `executor.ts:460`. Confirm at implementation time by matching the closest existing diagnostic voice.
- **`expect` field scope**: `definition.md` allows `${{ failure.* }}` "only in recovery handler tasks" but does not call out `expect` vs `with` separately. Plan: walk both, since both are template-bearing. If validation today rejects `failure.*` in `expect`, narrow the pass to `with` only. Verify against the server's `ValidateTaskExpectations` / definition validator during implementation.
- **Pre-loading prompt body when `variables.prompts` is missing**: in principle `variables.prompts` is always populated for workflow dispatches (the executor already relies on it). If a test harness omits it, the failure-context pass should fail loudly ("prompt body for `${{ prompts.<key> }}` not available in recovery context") rather than silently inlining an empty body. Decide the exact wording during implementation.
