# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: test-gap
  Scope: packages/web/src/entities/session/model/view/{chat,timeline,compact}.ts tests
  Evidence: Issue acceptance criterion #4 requires `transcript-state.test.ts` and related transcript specs to pass and also requires nearby tests to be added or migrated for the extracted projectors (`为拆出的 projector 补/迁移就近测试`). The candidate split the projectors into `packages/web/src/entities/session/model/view/chat.ts`, `timeline.ts`, and `compact.ts`, but added no co-located projector tests: `packages/web/src/entities/session/model/view/*.test.ts` has no files. The only projector coverage remains in the pre-existing monolithic `packages/web/src/entities/session/model/view.test.ts` at lines 114, 223, and 328. This leaves one explicit issue acceptance criterion unmet. [disallowed:broad-test-organization]
  SuggestedAction: Migrate the existing chat/timeline/compact `describe` blocks from `packages/web/src/entities/session/model/view.test.ts` into co-located tests under `packages/web/src/entities/session/model/view/`, or add equivalent nearby tests for the extracted projector modules while keeping the public `viewSessionEvents` contract covered.
  Verification: `glob packages/web/src/entities/session/model/view/*.test.ts` returned no files; `grep "describe\\('viewSessionEvents|buildChatView|buildTimelineView|buildCompactView" packages/web/src/entities/session/model --include "*.test.ts"` found projector suites only in `view.test.ts`. `npm run test:run -w packages/web` passed, so this is a missing acceptance-criterion coverage item rather than a runtime test failure.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

_None._

## Acceptance Criteria Evidence

- `view.ts` decomposition: `packages/web/src/entities/session/model/view.ts` is now a 125-line dispatcher/type entry point, and `view/chat.ts`, `view/timeline.ts`, `view/compact.ts`, and `view/helpers.ts` exist. SCC reports per-file complexity below 180: `view.ts` C=4, `chat.ts` C=69, `timeline.ts` C=47, `compact.ts` C=29, `helpers.ts` C=102.
- Tool-state de-duplication and relocation: `packages/web/src/widgets/session-transcript/model/transcript-tool-state.ts` contains a single `mergeToolPart` helper at lines 156-205 and re-exports `buildLiveToolDetails` from `../ui/tool-views/live-details` at line 272. `packages/web/src/widgets/session-transcript/ui/tool-views/live-details.ts` contains the relocated dispatcher. SCC reports `transcript-tool-state.ts` C=73.
- Public contract and behavior checks: `npm run typecheck -w packages/web` passed. `npm run test:run -w packages/web` passed with 295 files, 4416 passed tests, and 1 skipped test. `git diff --check master...HEAD` produced no whitespace errors.
- Security/data/migration review: the product change is frontend-only refactoring, with no server, runner, CLI, persistence, network, schema, or public protocol changes. No exposed secrets or new input execution paths were introduced.

<promise>FAIL</promise>
