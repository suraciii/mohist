# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/runner/tests/acp-agent.spec.ts`
  Evidence: Two updated tests still include stale names referencing `BeforeMohistContextWrapper` even though the wrapper was removed: `StringPromptContainingLiteralTemplateSyntax_IsNotTemplateRenderedBeforeMohistContextWrapper` at line 536 and `UsesFormPrompt_ActionResolvesThroughRegisteredLoaderBeforeMohistContextWrapper` at line 575. This does not affect behavior or acceptance criteria, but it makes the test intent less clear after the contract change.
  SuggestedAction: Rename those tests to describe the current no-wrapper behavior, for example `...WithoutMarkdownEnvelope`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: issue metadata
  Evidence: The issue body's Non-Goals section says issue-context injection is a separate child issue `#139`, which is self-referential because this review is for issue 139. The candidate consistently treats context re-injection as out of scope and removes only the markdown envelope path.
  SuggestedAction: Cross-link the actual follow-up issue for context re-injection when known.
  Status: out-of-scope

## Review Evidence

- Issue details were loaded with `mo issue show 139 --project-id proj_f6c141d63b6243bfbb481737b2243b87` and matched the supplied prompt assembly contract.
- Changed product files reviewed: `packages/runner/src/actions/acp-agent.ts`, `packages/runner/src/core/prompt.ts`, and `packages/runner/tests/acp-agent.spec.ts`; OpenSpec artifacts under `openspec/changes/issue-139/` were reviewed as context/evidence.
- `packages/runner/src/actions/acp-agent.ts:485` now calls `resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))` directly and passes the resolved `prompt` unchanged into `runAcpWorkflowAgentSession` at `packages/runner/src/actions/acp-agent.ts:491`.
- The old markdown and fallback helpers are absent from `packages/runner/src/actions/acp-agent.ts`; grep found no `buildPromptWithMohistContext`, `buildFallbackPrompt`, `resolveActionPrompt`, `## Mohist Issue Context`, or `## Task Prompt` prompt-building path in that file.
- Missing or whitespace prompt behavior is explicit at `packages/runner/src/actions/acp-agent.ts:489`, returning `ACP agent requires 'prompt'` before any ACP interaction.
- The contract is documented at `packages/runner/src/core/prompt.ts:5`, including text passthrough, object-to-XML rendering, loader dispatch, and the no-wrapper/no-fallback rule.
- Action-level regression coverage verifies string prompt byte-for-byte delivery at `packages/runner/tests/acp-agent.spec.ts:523`, object prompt XML delivery at `packages/runner/tests/acp-agent.spec.ts:548`, loader text/object dispatch at `packages/runner/tests/acp-agent.spec.ts:575` and `packages/runner/tests/acp-agent.spec.ts:592`, loader context inputs at `packages/runner/tests/acp-agent.spec.ts:613`, missing prompt failure at `packages/runner/tests/acp-agent.spec.ts:664`, and unknown loader pre-ACP failure at `packages/runner/tests/acp-agent.spec.ts:678`.
- Renderer/loader contract coverage remains in `packages/runner/tests/prompt-renderer.spec.ts`, including string identity, single-root XML, attrs, arrays, invalid tag names, loader resolution, loader input forwarding, and invalid loader returns.
- Built-in workflow prompts were checked in `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`; all ACP-agent task entries continue to declare explicit `prompt` values, so fallback removal does not break built-in workflow definitions.
- Verification passed: `npm -w packages/runner run typecheck` completed with no TypeScript errors.
- Verification passed: `npm -w packages/runner test` completed with 27 test files and 335 tests passing, including `tests/prompt-renderer.spec.ts`, `tests/openspec-task-prompt.spec.ts`, and `tests/acp-agent.spec.ts`.

<promise>PASS</promise>
