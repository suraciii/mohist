# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/runner/tests/acp/session-strategies.spec.ts`
  Evidence: The issue acceptance criteria require the corresponding tests to be organized by the split modules and that a single test file no longer exceed the healthy threshold. The post-build candidate split `packages/runner/tests/acp-agent.spec.ts` into `packages/runner/tests/acp/session-strategies.spec.ts`, `packages/runner/tests/acp/session-events.spec.ts`, `packages/runner/tests/acp/compaction.spec.ts`, and `packages/runner/tests/acp/liveness.spec.ts`, but `packages/runner/tests/acp/session-strategies.spec.ts:1` is still 954 lines. The workflow evidence itself records the nearest existing reference as `workspace.spec.ts` at 890 lines and notes this new file is larger, so the candidate does not satisfy the "single test file no longer exceeds healthy threshold" acceptance criterion. Repair would require a further behavioral-test reshuffle across runner lifecycle strategy files, which is broader than a small local review repair [disallowed:broad test organization change].
  SuggestedAction: Split `packages/runner/tests/acp/session-strategies.spec.ts` by the four lifecycle strategy clusters called out in the issue (`new`, `resume`, `reuse existing`, `ephemeral`) or otherwise document and enforce a concrete healthy threshold that this file satisfies.
  Verification: Re-run `npm run typecheck -w packages/runner` and `npm test -w packages/runner -- tests/acp/*.spec.ts tests/acp-tool-noise.spec.ts`, then confirm line counts for all ACP spec files are under the accepted threshold.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/runner/src/actions/acp/session-events.ts`, `packages/runner/src/actions/acp/compaction.ts`, `packages/runner/src/actions/acp/model-resolution.ts`
  Evidence: `session-events.ts` keeps local copies of compaction/model extraction logic at `packages/runner/src/actions/acp/session-events.ts:383` and `packages/runner/src/actions/acp/session-events.ts:398`, while exported helpers now live at `packages/runner/src/actions/acp/model-resolution.ts:86` and `packages/runner/src/actions/acp/compaction.ts:66`. This does not currently break behavior, but future fixes to extraction semantics can drift unless both copies are updated.
  SuggestedAction: In a follow-up refactor, remove the duplicated extraction logic through an acyclic shared seam or add focused tests that assert the local emitter extraction remains equivalent to the exported helpers.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: runner validation scripts
  Evidence: The issue/tasks reference `npm run test:run -w packages/runner`, but `packages/runner/package.json` exposes `test`, `build`, `dev`, and `typecheck`; `npm run test:run -w packages/runner` fails with `Missing script: "test:run"`. This is a repository/script-documentation mismatch rather than a product regression in the ACP refactor.
  SuggestedAction: Update runner validation documentation or add a `test:run` alias if that command is intended to be canonical.
  Status: out-of-scope

Validation performed:

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner -- tests/acp/session-strategies.spec.ts tests/acp/session-events.spec.ts tests/acp/compaction.spec.ts tests/acp/liveness.spec.ts tests/acp-tool-noise.spec.ts` passed: 5 files, 65 tests.
- `npm test -w packages/runner` passed: 34 files, 460 tests.

Acceptance evidence reviewed:

- Source structure exists: slim entry `packages/runner/src/actions/acp-agent.ts:1` plus focused modules under `packages/runner/src/actions/acp/` (`process.ts`, `session-strategies.ts`, `liveness.ts`, `compaction.ts`, `model-resolution.ts`, `session-events.ts`, `agent-config.ts`).
- Public surface remains re-exported from `packages/runner/src/actions/acp-agent.ts:9` through `packages/runner/src/actions/acp-agent.ts:12`.
- Complexity evidence in `openspec/changes/issue-250/progress.txt:152` reports all ACP modules below the runner package top three.
- ACP behavior regression coverage passes in targeted and full runner test runs.
- Test split exists, but `packages/runner/tests/acp/session-strategies.spec.ts:1` remains above the documented local healthy reference, causing the fail verdict.

<promise>FAIL</promise>
