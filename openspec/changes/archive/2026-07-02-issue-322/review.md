# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/web/src/pages/insights/model/delivery.ts:38` uses an unnecessary `as StageDurationStageDto[]` type cast on `stageDuration.stages`, which is already typed as `StageDurationStageDto[]` in the `StageDurationMetricsResponse` interface (`packages/web/src/entities/issue/api/stage-duration.ts:24`).
  Verification: `npm run typecheck -w packages/web` passes before and after the cast removal; the cast adds no type safety.
  Status: resolved — removed unnecessary cast.

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/ui/SignalSummary.tsx:198,264,266`
  Evidence: The ThroughputCard uses `本周完成 ${currentValue} 个` and the InvestmentCard uses `本周 ${spendText}`. Per design D1 the window is a 30-day trailing window, not a weekly window. The design states "Verdict copy says 'compared to the previous period' (window-agnostic)." The "本周" copy is misleading — users will expect a literal 7-day count but the data represents 30-day totals.
  SuggestedAction: Change "本周" to "本期" or another window-agnostic term consistent with the design D1 decision. The spec's acceptance criteria use "本周/上周" as illustrative examples, but the actual window is 30 days.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/model/delivery.ts:35-45` and `packages/web/src/pages/insights/model/index.ts:73-84`
  Evidence: `findSlowestStage` in `delivery.ts` and `deriveSlowestStageName` in `index.ts` implement identical logic (iterate stages, find max non-null `averageSeconds`). The exported `deliverySlowestStageName` function wraps `findSlowestStage` and is only exercised by unit tests; the production code path uses the inline `deriveSlowestStageName` in `index.ts`.
  SuggestedAction: Consolidate the two implementations. Use `deliverySlowestStageName` or a shared helper in `deriveSignalSummary` so there is a single source of truth for the slowest-stage derivation.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/model/delivery.ts:57-67`
  Evidence: The delivery verdict computes the current-window average cycle time by iterating all per-issue `points`, filtering null `cycleDays`, and computing the arithmetic mean. The server already computes this internally as part of `GetDeliveryTimesAsync`. While this is correct and intentional per the design (T-002 only adds the previous window to the response), a server-computed `currentAverageCycleDays` alongside `previousAverageCycleDays` would avoid the frontend re-aggregation and ensure the two averages use the same computation path.
  SuggestedAction: Consider adding a server-computed `currentAverageCycleDays` field to `DeliveryTimeMetricsResponse` for symmetry and to avoid potential floating-point divergence between frontend and backend computations. Defer to a later milestone.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/entities/issue/api/completion-trend.ts:14`
  Evidence: `sampleCount` in `CompletionTotalDto` is typed as `number` (non-nullable). However, when the server returns a JSON payload, ASP.NET Core serializes `int SampleCount` as a non-nullable integer. If a future deployment rolls back the server change, old servers won't include `sampleCount` at all, and the TypeScript type would incorrectly claim it is always present. This is low risk — the field is guarded by the optional `currentTotal?` / `previousTotal?` and model code checks `sampleCount` existence.
  SuggestedAction: If backcompat with pre-M1 servers is required, mark `sampleCount` as optional.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/web/src/entities/agent/api/cost-rollup.ts:21-22`
  Evidence: `currentWindow` and `previousWindow` are marked optional (`?`) in the TypeScript interface, and the corresponding verdict derivation code guards against undefined correctly. The server DTO (`AgentCostRollupDto`) always includes them after this change. The optional markers maintain backward compatibility with older servers. No action needed.
  SuggestedAction: None.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.Metrics.cs:17`
  Evidence: The `TimeProvider timeProvider` parameter is now injected via DI into the completion route handler, replacing the previous direct `DateTimeOffset.UtcNow` call (D4 fix). The `ServiceCollection` already registers `TimeProvider`; no startup change is needed. Three sibling metrics routes already inject `TimeProvider`, so this alignment is correct.
  SuggestedAction: None.
  Status: pre-existing

<promise>PASS</promise>
