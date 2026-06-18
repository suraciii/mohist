## Context

Issue 138 is the parent of the "Workflow Prompt Assembly, Context Injection & Task Lifecycle" epic. Its sibling, issue 139 (merged), already removed `buildPromptWithMohistContext`, the `## Mohist Issue Context` / `## Task Prompt` markdown envelope, and the ad-hoc fallback synthesizer, and unified all prompt assembly under `resolvePrompt`.

Current state of the prompt path:

- `acpAgentAction` (`packages/runner/src/actions/acp-agent.ts:482`) resolves the prompt via `resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))` and passes the result verbatim to `runAcpWorkflowAgentSession`. **No code injects issue title or body.**
- All 14 built-in `.prompt` templates already embed `mo issue show ${{ issue.number }} --project-id ${{ project.id }}`.
- The only registered `PromptLoader` is `openspecTaskPromptLoader` (`packages/runner/src/core/prompt-registry.ts:7`). It reads only `ctx.with` and ignores `ctx.issueNumber` and `ctx.variables`.
- `buildPromptLoaderContext` (`acp-agent.ts:680`) still threads `issueNumber: context.issueNumber ?? null` into the `PromptLoaderContext`. This field is dead for prompt resolution.
- `issueNumber` IS still used legitimately outside prompt text: session-open metadata (`acp-agent.ts:707`, `:734`) and the session-event record (`acp-agent.ts:1355`, as `issueId` for logging/timeline). These are retained.

So the "no code injects issue context" property is currently **emergent** — true only because the envelope was deleted, not because anything enforces it. Issue 138's job is to make it durable: codify it as a requirement (done in the spec delta), close the dead input vector, and add a regression guard.

## Goals / Non-Goals

**Goals:**

- Make "no code path injects issue title/body into the resolved prompt" a durable, tested contract — not an emergent accident.
- Remove the one residual thread that connects issue identity to prompt resolution (the unused `issueNumber` in the loader context), so the principle holds structurally.
- Add an action-level regression test asserting issue title/body are absent from the resolved prompt even when present in the run context.

**Non-Goals:**

- Prompt assembly format text-vs-XML — owned and completed by issue 139.
- Removing `issue.number` / `project.id` variable interpolation in prompt templates (explicitly preserved).
- Editing built-in `.prompt` template bodies — they are already correct.
- Restricting which variables the template-interpolation layer exposes (e.g. whether `issue.title`/`issue.body` are interpolatable) — deferred, see Open Questions.

## Decisions

### Decision 1: No runtime "scrubber"; codify + close the vector + test

The `acpAgentAction` → `resolvePrompt` path already satisfies the requirement. We do **not** add a runtime filter that strips known issue title/body from the resolved prompt. Such a filter would sit at the wrong layer, mask legitimate template authoring, and duplicate the spec.

Instead: (a) keep the spec requirement (added in the specs artifact), (b) remove the dead issue-identity input to prompt resolution (Decision 2), (c) add an action-level regression test (Decision 3).

- *Alternative considered:* runtime assertion that the resolved prompt excludes the run's known issue title/body. **Rejected** — fragile (an author may legitimately write a title-like string) and it re-introduces a post-processing step on the resolved prompt, the exact pattern issue 139 removed.

### Decision 2: Stop passing issue identity into prompt resolution

Remove `issueNumber` from the loader context built in `buildPromptLoaderContext` (`acp-agent.ts:688`). The only loader ignores it, so no prompt-resolution behavior changes; this just removes the last structural path by which issue identity could reach prompt assembly. Also drop the now-unused `issueNumber?` field from the `PromptLoaderContext` interface (`packages/runner/src/core/prompt.ts:22`) for consistency — confirmed only `buildPromptLoaderContext` populates it and no loader reads it. `title?` / `stage?` remain on the interface as generic loader context.

Keep `issueNumber` on the session-open calls (`acp-agent.ts:707`, `:734`) and the session-event record (`:1355`). Those are session metadata and logging, not prompt text, and are out of scope.

- *Alternative A:* keep `issueNumber` on the interface "for future loaders." **Rejected** (YAGNI). Templates already construct CLI context via `${{ }}` interpolation against `variables`; loaders assemble structured task prompts and have never needed issue identity.
- *Alternative B:* remove only from the constructed object, leave the interface field. **Rejected** as it leaves a misleading contract suggesting loaders receive issue identity.

### Decision 3: Regression test at the action boundary

Extend the existing verbatim-passthrough test block (`packages/runner/tests/acp-agent.spec.ts:523-534`). Add a case where the fixture's `variables` carry an `issue` object (with a distinctive title and body) and `issueNumber` is set, then assert the resolved prompt text equals the declared prompt and contains neither the title nor the body. Keep the existing `## Mohist Issue Context` / `## Task Prompt` negative assertions.

- *Alternative considered:* a pure unit test on `resolvePrompt`. **Rejected** — the historical risk lived at the action boundary (the envelope was applied there), so the guard belongs at the action level, not the dispatcher.

### Decision 4: Built-in template convention enforced by spec, not by code edits

No `.prompt` body changes — all 14 builtins already embed `mo issue show`. The new spec scenario ("Issue context is fetched via a CLI instruction embedded in the template") makes drift a spec violation caught at review. A mandatory server-side scan test is **optional** (see Open Questions), kept out of the core change to avoid cross-project scope creep.

## Risks / Trade-offs

- `[Dropping issueNumber from PromptLoaderContext is a contract change]` -> Mitigation: the field is optional, only one site populates it, and no loader reads it. Blast radius is the single test file that imports the action. A future loader needing issue identity can re-add it with explicit justification.
- `[No runtime scrubber means a regressed code path could silently re-inject]` -> Mitigation: the spec requirement plus the action-level regression test pin the boundary where the old envelope lived.
- `[Template ${} interpolation could still surface issue.title/body if those variables are exposed]` -> Mitigation: out of scope (Non-Goal preserves only `issue.number`/`project.id`); the requirement prohibits CODE injection, not explicit author interpolation. Flagged in Open Questions for a follow-up.
- `[issueNumber still reaches session metadata/logging]` -> Not a prompt-text risk; explicitly retained. No exposure to the LLM prompt.

## Migration Plan

No data migration, no CLI/API contract change, no persistent-state impact. Single PR, deploy in one step:

1. Spec delta (already written in the specs artifact).
2. Drop `issueNumber` from `buildPromptLoaderContext` and from the `PromptLoaderContext` interface.
3. Add the action-level regression test.

**Rollback:** revert the PR. The spec requirement is the durable artifact; because no correct template's behavior changes, rollback is user-invisible and safe.

## Open Questions

- Should the template-interpolation layer expose `issue.title` / `issue.body` as variables at all? If yes, a template author could `${{ issue.body }}` them into a prompt — not code injection, but it blurs the "prompts carry no issue content" intent. Recommend a small follow-up to decide whether the variables contract should be restricted to identifiers (`issue.number`, `project.id`). Deferred per the proposal's Non-Goals.
- Should a mandatory server-side test assert every `Prompts/builtins/*.prompt` embeds `mo issue show`? Decide during the build phase based on where builtin-prompt tests conventionally live (server vs. a shared contract test).
