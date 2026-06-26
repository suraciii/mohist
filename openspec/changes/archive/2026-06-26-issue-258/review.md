# Review Report

## Result: PASS

Acceptance criteria evidence: `DashboardPage.tsx:64-84` renders the top-to-bottom stack as headline, hero, then remaining zones. `FactoryStatusHeadline.tsx:31-74` renders runner, in-flight, awaiting-approval, shipped-today, and reserved today-cost fields. `factory-status.ts:22-52` implements the specified counts from `useIssues` data and `agentStatus.runnerAvailable`. `AttentionHero.tsx:80-110` remains the full-width attention surface, and `AttentionHero.tsx:140-199` keeps inline Open, Approve, and Resume actions. Tests cover the dashboard order and slot placement in `DashboardPage.test.tsx:126-186`, headline fields and placeholder in `FactoryStatusHeadline.test.tsx:97-153`, derivation in `factory-status.test.ts:73-143`, and the mounted Hero behavior in `AttentionHero.test.tsx`.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: hook-order-correctness | regression-coverage
  Evidence: Before repair, `AttentionHero` returned `LoadingState` before calling the approve/resume `useMutation` hooks. Once issue data loaded, the same mounted Hero would call additional hooks on the next render, violating React hook order during the normal loading-to-loaded query transition. The repair moves both mutation hooks above the loading early return in `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:52-70` and adds a regression test that rerenders from loading to all-clear in `packages/web/src/widgets/attention-hero/ui/AttentionHero.test.tsx:576-590`.
  Verification: `npm run test:run -w packages/web -- src/widgets/attention-hero/ui/AttentionHero.test.tsx`; `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- src/widgets/attention-hero/ui/AttentionHero.test.tsx src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/factory-status/model/factory-status.test.ts src/widgets/factory-status/ui/FactoryStatusHeadline.test.tsx`; `npm run test:run -w packages/web`; `git diff --check`.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/widgets/factory-status/ui/FactoryStatusHeadline.tsx
  Evidence: The headline intentionally renders `Unavailable` whenever `agentStatus?.runnerAvailable !== true`, including the initial unknown/loading state. This follows the design's neutral-unavailable leaning and is not a blocker for this issue, but a distinct `Unknown`/loading presentation would reduce transient false-negative runner signals on first paint.
  SuggestedAction: Consider separating unknown runner status from known-unavailable once the dashboard loading semantics are refined.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: packages/web Vitest configuration
  Evidence: Every web test run prints `DEPRECATED  test.poolOptions was removed in Vitest 4. All previous poolOptions are now top-level options.` This is unrelated to the dashboard candidate and tests still pass, but it indicates the test config needs a Vitest 4 cleanup.
  SuggestedAction: Update the Vitest configuration to the Vitest 4 pool option shape in a separate maintenance change.
  Status: pre-existing

<promise>PASS</promise>
