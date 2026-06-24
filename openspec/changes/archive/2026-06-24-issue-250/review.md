# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: import cleanup
  Evidence: The post-fix split strategy tests carried unused imports copied from the former large file. Removed unused `node:fs/promises`, `node:path`, `node:os`, `vi`, `PromptLoaderRegistry`, `PromptLoader`, `PromptLoaderContext`, and unused support imports from `packages/runner/tests/acp/session-strategies-reuse.spec.ts`, `packages/runner/tests/acp/session-strategies-resume.spec.ts`, and `packages/runner/tests/acp/session-strategies-liveness.spec.ts`.
  Verification: `npm run typecheck -w packages/runner`; `npm test -w packages/runner -- tests/acp/session-strategies.spec.ts tests/acp/session-strategies-reuse.spec.ts tests/acp/session-strategies-resume.spec.ts tests/acp/session-strategies-liveness.spec.ts tests/acp/session-events.spec.ts tests/acp/compaction.spec.ts tests/acp/liveness.spec.ts tests/acp-tool-noise.spec.ts`; `npm test -w packages/runner`.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: runner validation scripts
  Evidence: The issue/tasks text still references `npm run test:run -w packages/runner`, while the runner package exposes `test`, `build`, `dev`, and `typecheck`. This is a documentation/script-alias mismatch outside the ACP adapter refactor; review validation used the existing `npm test -w packages/runner` command.
  SuggestedAction: Align runner validation documentation with `npm test -w packages/runner` or add a `test:run` alias in a separate cleanup.
  Status: out-of-scope

Acceptance evidence reviewed:

- ACP source is split into a 46-line slim entry at `packages/runner/src/actions/acp-agent.ts:1` plus focused modules: `process.ts` 89 lines, `session-strategies.ts` 581 lines, `liveness.ts` 243 lines, `compaction.ts` 103 lines, `model-resolution.ts` 103 lines, `session-events.ts` 483 lines, and `agent-config.ts` 48 lines.
- Public surface remains frozen via re-exports at `packages/runner/src/actions/acp-agent.ts:9` through `packages/runner/src/actions/acp-agent.ts:12` for `AcpProcessHandle`, `setAcpProcessFactoryForTest`, `AcpProcessFactory`, `resolveCompactionConfig`, `defaultCompactionConfig`, `CompactionConfig`, and `CompactionStrategy`.
- Test organization now satisfies the prior size finding: `wc -l packages/runner/tests/acp/*.ts` shows the largest ACP spec is `packages/runner/tests/acp/session-strategies.spec.ts` at 657 lines, below the documented 890-line local reference; added focused strategy files are 134, 47, and 191 lines.
- Complexity evidence in `openspec/changes/issue-250/progress.txt:152` reports all ACP modules below the runner package top three, with `session-strategies.ts` highest at 4th and complexity 107.
- No behavioral/API/security/migration concerns found: the refactor preserves ACP action imports, keeps external consumer paths unchanged, uses fake ACP/server processes in tests, and introduces no new public contract, database, dependency, or protocol changes.

Validation performed:

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner -- tests/acp/session-strategies.spec.ts tests/acp/session-strategies-reuse.spec.ts tests/acp/session-strategies-resume.spec.ts tests/acp/session-strategies-liveness.spec.ts tests/acp/session-events.spec.ts tests/acp/compaction.spec.ts tests/acp/liveness.spec.ts tests/acp-tool-noise.spec.ts` passed: 8 files, 65 tests.
- `npm test -w packages/runner` passed: 37 files, 460 tests.

<promise>PASS</promise>
