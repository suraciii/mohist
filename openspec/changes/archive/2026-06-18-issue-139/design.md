## Context

The runner currently builds LLM prompts through three code paths in `packages/runner/src/actions/acp-agent.ts`:

1. `resolveActionPrompt` (acp-agent.ts:721) dispatches to `resolvePrompt` (core/prompt.ts:55) — the **correct** contract path. `resolvePrompt` already implements: string → verbatim text, object → XML via `renderStructuredPrompt`, `uses`+`with` → registered `PromptLoader`.
2. `buildFallbackPrompt` (acp-agent.ts:704) — synthesizes an ad-hoc markdown prompt from loose `title`/`description`/`acceptanceCriteria`/`dependsOn`/`output`/`notes` fields when `with.prompt` is absent. Bypasses the contract.
3. `buildPromptWithMohistContext` (acp-agent.ts:559) — post-wraps any resolved prompt in a `## Mohist Issue Context` / `## Task Prompt` markdown envelope. Bypasses the contract.

`acpAgentAction` (acp-agent.ts:482) chains all three: `resolveActionPrompt` → `buildPromptWithMohistContext` → `runAcpWorkflowAgentSession`. The result is that prompt shape depends on which path ran, not on a single rule.

The dispatcher (`resolvePrompt`), the `PromptLoaderRegistry`, `renderStructuredPrompt`, and the default `openspecTaskPromptLoader` (core/prompt-registry.ts) are already correct and unit-tested. The work is to **delete the two bypass paths and make the action route solely through the dispatcher**, plus document the contract. A grep of `mohist-default.workflow.yaml` confirms **every built-in task already declares an explicit `prompt`** (`${{ prompts.* }}`, inline strings, or inline objects), so the fallback path is exercised only by a unit test, never by real workflows.

Issue context injection (the `buildPromptWithMohistContext` envelope) is explicitly a **Non-Goal** of this issue — re-injection is owned by a separate child issue.

## Goals / Non-Goals

**Goals:**
- Make `resolvePrompt` the sole entry point for prompt assembly in the agent action.
- Remove `buildFallbackPrompt` and `buildPromptWithMohistContext` and their helpers.
- Document the "text → text, object → XML, loader → dispatch" contract at the source.
- Preserve verbatim text behavior for all existing `.prompt` templates and inline YAML strings.
- Update tests to assert the contract end-to-end through the action.

**Non-Goals:**
- Re-implementing issue-context injection (separate child issue).
- Changing the `.prompt` template authoring format or frontmatter.
- Making XML mandatory.
- Altering `resolvePrompt`/`renderStructuredPrompt` internals — they already satisfy the contract.

## Decisions

### Decision 1: Collapse the action to a single `resolvePrompt` call

`acpAgentAction` will call `resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))` once and pass the result directly to `runAcpWorkflowAgentSession`. `resolveActionPrompt`, `buildPromptWithMohistContext`, and the post-wrap step are removed.

- **Rationale**: eliminates the bypass; the action no longer has a prompt-shape opinion.
- **Alternatives considered**:
  - Keep `buildPromptWithMohistContext` but route it through `resolvePrompt`. **Rejected** — it would still be markdown wrapping layered on top, restating the inconsistency.
  - Introduce a new "assemble" wrapper around `resolvePrompt`. **Rejected** — `resolvePrompt` is already the wrapper; another layer re-creates the problem.

### Decision 2: Remove `buildFallbackPrompt`; missing `prompt` is a hard error

When `with.prompt` is absent/null, the action returns `{ status: "failure", message: "ACP agent requires 'prompt'" }` (the message currently used when the resolved prompt is empty). No synthesis from `title`/`description`/etc.

- **Rationale**: the issue's acceptance criterion requires fallback removal "in favor of explicit prompt specs in task definitions." Every built-in task already declares one, so no real workflow breaks.
- **Alternatives considered**:
  - Route the fallback through `resolvePrompt` by building an object from the loose fields. **Rejected** — it hides prompt shape in code rather than the task definition, the exact inconsistency being fixed.
  - Keep a default text prompt. **Rejected** — contradicts the acceptance criterion.

### Decision 3: Leave a documented seam for issue-context re-injection

Removing the envelope means the agent no longer receives the issue body at this layer. This is intentional and out of scope. The contract's `PromptLoader` extension point is the designated future home: a loader (e.g. `mohist-issue-context`) can return a structured object that renders as XML, respecting the text/object rule. This design adds no loader; it only documents the seam.

- **Rationale**: enforces the contract without doing the separate child issue's work.
- **Alternatives considered**: implement the context loader now. **Rejected** — scope creep; the child issue owns injection design (whether to inject, what shape, opt-in mechanism).

### Decision 4: Document the contract at the source, not in a separate doc

Add a docblock above `PromptSpec`/`resolvePrompt` in `core/prompt.ts` stating the authoritative rule: input type determines format; all consumers route through `resolvePrompt`; no markdown wrapping.

- **Rationale**: colocation keeps the contract visible to anyone editing the module; a separate architecture doc rots.
- **Alternatives considered**: a `docs/prompt-assembly.md`. **Rejected** — drifts from code.

### Decision 5: Test the contract through the action, not just the dispatcher

The dispatcher's rendering rules are already covered by `prompt.spec.ts`. The action-level tests will be rewritten to assert the contract end-to-end: a string `prompt` reaches the ACP session verbatim (no envelope); an object `prompt` reaches the session as XML; a missing `prompt` fails with the clear error. The three obsolete tests (`OpenSpecTaskWithoutPrompt_ActionBuildsPromptFromTaskFields`, `IssueVariablesPresent_ActionPrependsIssueContextToPrompt`, `IssueVariablesMissing_PromptContextBuilderLeavesPromptUnchanged`) and the `buildPromptWithMohistContext` import are removed.

- **Rationale**: the acceptance criterion requires the rule be "documented and tested" at the assembly level, not only the renderer.
- **Alternatives considered**: keep an envelope test behind a flag. **Rejected** — the envelope no longer exists.

## Risks / Trade-offs

- `[Removing fallback breaks tasks relying on loose title/description fields]` -> Mitigation: grep confirms no built-in task relies on it; only a unit test does. The new hard error makes any undiscovered case fail loudly rather than silently synthesize a wrong prompt.
- `[Removing the issue-context envelope changes agent behavior — agents no longer see the issue body]` -> Mitigation: explicitly out of scope and documented as a seam; the child issue re-adds context via a loader. Called out in the migration plan and commit message.
- `[Dropping the exported buildPromptWithMohistContext symbol is a breaking change]` -> Mitigation: the symbol is internal runner code; the only external import is the test file. No public API consumer.
- `[buildPromptLoaderContext currently passes with: {} and ignores the task's with for loader dispatch]` -> Mitigation: out of scope (loader input wiring is unchanged); flagged as an open question if a future loader needs task inputs.

## Migration Plan

1. Edit `packages/runner/src/actions/acp-agent.ts`:
   - Replace the `resolveActionPrompt` + `buildPromptWithMohistContext` chain in `acpAgentAction` with a single `resolvePrompt` call.
   - Missing/empty resolved prompt → return the existing `"ACP agent requires 'prompt'"` failure.
   - Delete `resolveActionPrompt`, `buildFallbackPrompt`, `buildPromptWithMohistContext`, `promptContextField`, `valueSection`, `formatValue`.
2. Add the contract docblock above `PromptSpec` / `resolvePrompt` in `packages/runner/src/core/prompt.ts`.
3. Update `packages/runner/tests/acp-agent.spec.ts`: drop the `buildPromptWithMohistContext` import and the three obsolete tests; add text-passthrough, object→XML, and missing-prompt-fails tests at the action level.
4. Verify: `npm -w packages/runner test` and `npm -w packages/runner run lint` / typecheck. Existing `prompt.spec.ts` rendering tests must remain green unchanged.
5. Grep task definitions for any `.prompt`-less task that would now fail; none expected.

**Rollback**: revert the commit. The two helpers and the markdown envelope are restored verbatim (the change is purely deletions plus one docblock), so rollback is clean and complete.

## Open Questions

- Should `buildPromptLoaderContext` forward the task's `with` into the loader context (`with: context.with ?? {}` instead of `with: {}`)? Currently loaders receive only `variables`. Not needed for this issue (no new loader is added), but the child issue's context loader may need task inputs. Defer to that issue.
- Should the missing-`prompt` error message name the offending task/stage for easier diagnosis? Likely yes, but the current message is reused as-is here to keep the change minimal; enriching it can be a follow-up.
