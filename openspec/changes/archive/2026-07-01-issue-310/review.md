# Review Report

## Result: PASS

Post-build candidate snapshot has no unresolved blocking findings.

Acceptance criteria evidence:

- `IssueDetailPage` is now a 453-line orchestration component: data/state hooks at `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:36`, mutation hook wiring at `:49`, query wiring at `:79`, action-state computation at `:107`, layout composition at `:133`, main/right column slots at `:316`, and extracted card/section mounts through `:437`.
- The duplicated click-outside + auto-dismiss behavior is consolidated in `packages/web/src/pages/issue-detail/model/useConfirmOutsideClick.ts:18`; `IssueActionsCard` uses that hook for both force-stop and stop confirmation panels at `packages/web/src/pages/issue-detail/ui/cards/IssueActionsCard.tsx:57` and `:61`.
- The `data-testid` set is unchanged: `git show master:...IssueDetailPage.tsx | rg -o 'data-testid="[^"]+"' | sort -u | wc -l` = 43, current page + extracted product modules = 43, and the set diff is empty. Existing occurrence count changed from 46 in one file to the same unique set split across the extracted modules, which is expected for this refactor.
- Capacity gating remains derived from `agentStatus.capacity` at `packages/web/src/pages/issue-detail/model/actionsState.ts:107` and rendered through the preserved start-button disabled/text states at `packages/web/src/pages/issue-detail/ui/cards/IssueActionsCard.tsx:155`.
- Navigation URLs remain page-owned and unchanged: View files / View all commits route to `/issues/${issueNumber}/files` from `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:324` and `:329`, and Ask Agent routes to `/agent-sessions/new?issue=${encodeURIComponent(issueNumber)}` at `:429`.
- Line/complexity reduction verified with `wc -l` and `scc`: `IssueDetailPage.tsx` is 453 lines, 416 code lines, complexity 64 versus the issue baseline 1375 lines / complexity 211.
- Verification passed locally: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- src/pages/issue-detail` (7 files / 132 tests); `npm run test:run -w packages/web` (236 files / 3643 passed / 1 skipped); `npm run build -w packages/web`; `git diff --check master...HEAD`.

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: `npm run build -w packages/web` / third-party dependency output
  Evidence: The production build succeeds, but Rollup emits existing warnings for `../../node_modules/@microsoft/signalr/dist/esm/Utils.js` PURE annotations at lines 190 and 208. This is dependency/build-tool noise and not introduced by issue-310 product files.
  SuggestedAction: Track separately only if the team wants a warning-clean web build.
  Status: out-of-scope

<promise>PASS</promise>
