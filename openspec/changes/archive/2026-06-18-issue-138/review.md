# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins
  Evidence: All 14 built-in `.prompt` templates currently include `mo issue show ${{ issue.number }} --project-id ${{ project.id }}` at line 10, satisfying issue AC3 and the new spec scenario. This is verified by review/search rather than an automated guard, so future drift would rely on spec review catching it.
  SuggestedAction: Consider a future server-side guard test that scans built-in prompt templates requiring issue context and asserts they include the CLI instruction.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: packages/runner/tests/push.spec.ts and packages/runner/tests/issue-112-regression.spec.ts
  Evidence: `npm test -w packages/runner` fails in 11 push/merge tests because `setMergeGitRunnerForTest` / `setMergeConflictResolverForTest` are imported from `packages/runner/src/actions/registry.ts` but are not exported functions. The issue-relevant suites pass, including `tests/acp-agent.spec.ts` and `tests/prompt-renderer.spec.ts`; no reviewed prompt-assembly source references these missing hooks.
  SuggestedAction: Fix the push/merge test hooks in a separate change so the full runner suite is green again.
  Status: out-of-scope

## Verification Notes

- Issue AC1 is satisfied: `buildPromptWithMohistContext` is absent, `acpAgentAction` passes `resolvePrompt(...)` output directly to `runAcpWorkflowAgentSession`, and only the expected negative test string remains in `packages/runner/tests/acp-agent.spec.ts:532`.
- Issue AC2 is satisfied: `PromptLoaderContext` has no `issueNumber` field in `packages/runner/src/core/prompt.ts:15`, `buildPromptLoaderContext` omits it in `packages/runner/src/actions/acp-agent.ts:680`, and `StringPrompt_ActionDoesNotInjectIssueTitleOrBody` asserts title/body absence in `packages/runner/tests/acp-agent.spec.ts:536`.
- Legitimate non-prompt issue metadata remains intact: `context.issueNumber` still flows to session-open metadata in `packages/runner/src/actions/acp-agent.ts:706` and `packages/runner/src/actions/acp-agent.ts:733`, plus the session-event `issueId` in `packages/runner/src/actions/acp-agent.ts:1354`.
- Regression coverage is present: loader-context assertions no longer include `issueNumber` in `packages/runner/tests/acp-agent.spec.ts:638`, `packages/runner/tests/acp-agent.spec.ts:667`, and `packages/runner/tests/prompt-renderer.spec.ts:265`.
- Verification run: `npm run typecheck -w packages/runner` passed.
- Verification run: `npm test -w packages/runner` failed only in the out-of-scope push/merge tests noted above; 27 test files and 330 tests passed, including the prompt-assembly suites.

<promise>PASS</promise>
