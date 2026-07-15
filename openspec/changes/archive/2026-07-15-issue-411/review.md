# Review Report

## Result: PASS

The post-repair candidate meets the issue acceptance criteria. `SessionDetailShell.tsx:359-397` reserves a compact-only 120px transcript floor while retaining the split reading layout and composer. `SessionDetailShell.tsx:605-675` preserves the session name, status, stage, and cancel trigger while hiding secondary metadata below `md`; `SessionUsageSummary.tsx:34-53` similarly reduces only secondary usage detail. The recovery path wraps at compact widths and constrains inline errors (`SessionDetailShell.tsx:176-203`, `SessionRecoveryActions.tsx:211-220`) without changing recovery gating or lifecycle behavior.

Browser coverage in `packages/web/tests/browser/coder-session-compact-viewport.spec.ts:309-421` verifies both `375x667` and `320x568`: a >=120px independently scrollable transcript, fixed header/usage/composer positions during scroll, recovery controls and active follow-up inputs above navigation, transcript clearance, a real cancel dialog, and a long recovery error without horizontal overflow. Desktop restoration is expressed by the `md:` classes and preserved evidence tests.

## Repaired Items

None performed during this review.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

None.

Verification: `git diff --check`, `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` (334 files, 4,665 tests), and `npm run test:browser -w packages/web` (52 Chromium tests) all passed.

<promise>PASS</promise>
