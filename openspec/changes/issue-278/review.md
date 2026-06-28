# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/epic-detail/model/advancement.ts`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: A real external prerequisite blocker from the Epic detail API is represented as both `startBlocker.kind === 'waiting-for'` and a non-empty `externalPrerequisites` list. The detail path copies `issue.Blocker` into `LinkedIssueDto.StartBlocker` at `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:247` and also builds `ExternalPrerequisites` at `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:249`; `IssueQuerier` computes waiting blockers from undelivered prerequisites at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:1252` and `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:1389`. The new client classifier only returns `external-prerequisite-blocker` when `candidate.startBlocker === null` at `packages/web/src/pages/epic-detail/model/advancement.ts:90`, so the normal API shape falls through to `idle-no-next` or `running-but-idle`. The UI then renders generic copy at `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:733` instead of the required external-prerequisite blocker copy/link. This fails the acceptance criterion that draft blocker and external prerequisite blocker states have clear distinct wording, and that idle/running no-next states explain why. [disallowed:product-behavior-change]
  SuggestedAction: Treat an undelivered candidate with non-empty `externalPrerequisites` as an external-prerequisite blocker even when the blocker is `waiting-for`, ideally only when the waiting issue number is one of the external prerequisite numbers. Keep `canStart` as the guard against showing blocker copy for a truly startable `progress.nextIssue`. Add regression coverage for the detail-API shape: `canStart: false`, `startBlocker: { kind: 'waiting-for', issue: { number: 99 } }`, and `externalPrerequisites: [{ number: 99, ... }]`.
  Verification: Run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx`, and the targeted Playwright first-fold/mobile spec after the fix.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/web/src/pages/epic-detail/model/advancement.test.ts`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx`
  Evidence: The external-prerequisite tests only cover a synthetic state where `startBlocker` is `null` while `canStart` is false and `externalPrerequisites` is non-empty (`packages/web/src/pages/epic-detail/model/advancement.test.ts:119`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:3207`). They do not cover the realistic detail API shape described in item-1, so the acceptance-critical external blocker path can regress while all current tests pass. [disallowed:test-coverage-change]
  SuggestedAction: Add pure-model and page-level tests using the realistic `waiting-for` blocker plus `externalPrerequisites` shape, and assert both distinct external-prerequisite copy and prerequisite navigation links.
  Verification: Run `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx`.
  Status: open

- [ID: item-3]
  Severity: cleanup
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx`
  Evidence: The changed test file contains several malformed indentation blocks such as `progress:` at `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:466`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:499`, and `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:741`. This does not break TypeScript, but it makes an already large test file harder to review and maintain.
  SuggestedAction: Reformat the affected object literals or run the repository's formatter if one is available.
  Verification: Run `git diff --check` and the focused Epic detail tests.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `packages/web` Vitest configuration
  Evidence: `npm run test:run -w packages/web` reports: `DEPRECATED  test.poolOptions was removed in Vitest 4`. This is a repo test configuration warning, not introduced by the Epic detail product behavior itself.
  SuggestedAction: Update the Vitest config to the Vitest 4 pool option shape in a separate cleanup.
  Status: pre-existing

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 173 files, 2554 passed, 1 skipped.
- `npm run test:e2e -w packages/web -- tests/e2e/epic-detail-mobile-overflow.spec.ts` passed: 14 tests.
- `git diff --check master...HEAD` passed.

<promise>FAIL</promise>
