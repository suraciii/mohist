# Review — Issue #489 (Issue Watch / `mo issue watch`)

**Reviewer:** change reviewer (reviewer, not fixer)
**Artifacts changed:** all product deliverables listed in `progress.txt` under T-001 through T-004, cross-checked against issue #489, `design/issue-watch.md`, the two capability specs, and `design.md` D1–D9.

**Verdict: PASS** — the change satisfies the issue's acceptance criteria, conforms to the design decisions, and covers every spec scenario with tested code. No blocking findings.

## Change summary

Implemented in four stacked tasks:

1. **T-001** — `WatchEntry` domain model, `WatchEntryRow` flat-column persistence, EF migration, `WatchEntryStore` with the full state machine (`add`/`remove`/`list`) and active-Agent validation. 9 unit tests cover every transition + validation failure.
2. **T-002** — API routes (`POST`/`DELETE` `.../watch`) returning the re-enriched `IssueReadModel`, projection into `IssueQuerier.EnrichAsync` (detail + list paths), `IssueListItem` mirroring. 14 spec tests cover the full state machine + projection on detail + list + error codes.
3. **T-003** — `RoutingDispatchHandler` gains muted suppression inside the rule loop and a watching-launch pass after the rule loop; `rules.Count == 0` early return removed; per-delivery `HashSet` dedup; built-in watch prompt; `watch:`-prefixed `TriggerRuleId`; `RecordWatchPreflightFailureAsync` mirroring the routing preflight path. 11 spec tests cover every dispatch scenario.
4. **T-004** — CLI `mo issue watch add | remove | list` command group, `IssueShow` rendering with `watching:`/`muted:` sections, focused two-group `list` view; Web `Issue` TS interface + read-only `CollapsibleRailCard` entries with tests (CLI: 11 facts; Web: 3 facts + typecheck).

Verification: `npm run verify` passed cleanly (server 6,088 tests, Web 5,130 tests, Runner 1,383 tests; build + boundary checks green).

## Design decision review

| Decision | Verdict | Notes |
|----------|---------|-------|
| D1 — flat-column side relation | Compliant | `WatchEntryRow` mirrors `RoutingRuleRow`; `WatchEntryStore` IScopedService; migration matches `20260718100000_AddRoutingRules.cs` style. |
| D2 — route calls store, returns re-enriched model | Compliant | `IssueRoutes.Watch.cs` calls `WatchEntryStore` directly (not `IIssueGrain`); both POST/DELETE return `issuesQuery.GetAsync(...)`. |
| D3 — remove creates muted unconditionally | Compliant | `WatchEntryStore.RemoveAsync` never verifies covering rules; test `Remove_CreatesMutedWhenNoDeclarationExists` confirms. |
| D4 — suppression in handler, not evaluator | Compliant | `IsMutedForEvent` gate sits after the rule-match loop and before `LaunchRoutedAsync`; evaluator unchanged. |
| D5 — watch launch folded into RoutingDispatchHandler | Compliant | `LaunchWatchingAgentsAsync` runs after the rule loop; `rules.Count == 0` early return removed (ternary computes empty outcomes, foreach is a no-op, watch pass still fires). |
| D6 — batched projection in EnrichAsync | Compliant | `ApplyWatchProjectionAsync` called from both `EnrichAsync` (detail) and `ApplyRelationshipProjectionsAsync` (list); `IssueListItem` mirrors the fields. |
| D7 — handler-level HashSet dedup | Compliant | `HashSet<string> launchedAgentIds` tracks per-delivery launches; watch pass skips agents already in the set. Muted agents are also added to the set — a defensive no-op since mute+watching can't coexist per the unique index. |
| D8 — `watch:`-prefixed TriggerRuleId | Compliant | `WatchRuleIdPrefix = "watch:"`; watch launches pass `watch:{agentId}` as `ruleId`; `RecordWatchPreflightFailureAsync` uses the same prefix for stable grain keys. |
| D9 — CLI command group + state-as-truth | Compliant | `BuildWatch` → `add`/`remove`/`list`; `ResolveAgentAsync` promoted to `internal`; `add`/`remove` render `IssueShow`; `list` renders focused two-group via `IssueWatchList` table shape. |

## Spec scenario coverage

### `issue-watch` spec (7 scenarios in `specs/issue-watch/spec.md`)

| Scenario | Coverage |
|----------|----------|
| Unique state per issue-agent pair | EF unique index + `WatchEntryStore` ensures at most one row per triple |
| Add with no prior declaration | `WatchEntryStoreSpecs.Add_WithNoPriorDeclaration_CreatesWatchingEntry` |
| Add unmutes a muted agent | `WatchEntryStoreSpecs.Add_TransitionsMutedToWatching` |
| Add is idempotent when already watching | `WatchEntryStoreSpecs.Add_IsIdempotentWhenAlreadyWatching` |
| Remove a watching declaration | `WatchEntryStoreSpecs.Remove_DeletesWatchingEntry` |
| Remove records a mute | `WatchEntryStoreSpecs.Remove_CreatesMutedWhenNoDeclarationExists` |
| Remove is idempotent when already muted | `WatchEntryStoreSpecs.Remove_IsIdempotentWhenAlreadyMuted` |
| List separates watching and muted | `WatchEntryStoreSpecs.ListAsync_ReturnsSeparateGroupsByState` |
| Reject unknown/archived agent | `WatchEntryStoreSpecs.Add_RejectsUnknownAgent` + `Add_RejectsArchivedAgent` |
| CLI view / Web detail projection | `IssueWatchApiSpecs` (detail+list projection), `CliIssueWatchSpecs` (view/list render), `IssueDetailPage.reference-rail.watch.test.tsx` |

### `issue-watch-dispatch` spec (8 scenarios in `specs/issue-watch-dispatch/spec.md`)

| Scenario | Coverage |
|----------|----------|
| Launch on approval-requested | `IssueWatchDispatchSpecs.WatchLaunch_OnApprovalRequested_LaunchesWatchingAgentViaRoutedPath` |
| Launch on run-failed | `IssueWatchDispatchSpecs.WatchLaunch_OnRunFailed_LaunchesWatchingAgentViaRoutedPath` |
| No launch on other event types | `IssueWatchDispatchSpecs.WatchLaunch_OnUnrelatedEventType_DoesNotLaunch` |
| Event without issue does not trigger | `IssueWatchDispatchSpecs.WatchLaunch_OnEventWithoutIssue_DoesNotLaunch` |
| Mute suppresses rule hit | `IssueWatchDispatchSpecs.MutedSuppression_OnMutedAgent_SkipsRoutingRuleLaunch` |
| Mute does not leak | `IssueWatchDispatchSpecs.MutedSuppression_DoesNotLeakToOtherIssues` |
| Rule+watch single launch | `IssueWatchDispatchSpecs.RuleAndWatch_CoincideOnSameEvent_LaunchAgentExactlyOnce` |
| Replay no double-launch | `IssueWatchDispatchSpecs.EventReplay_UnderSameConfiguration_LaunchesAgentOnce` |
| Built-in prompt | `IssueWatchDispatchSpecs.WatchLaunch_UsesBuiltInPrompt_RegardlessOfStoredWatchEntry` |
| Zero routing rules + watch | `IssueWatchDispatchSpecs.WatchLaunch_WithZeroRoutingRules_StillFiresOnWatchEvent` |
| Archived agent skipped | `IssueWatchDispatchSpecs.WatchLaunch_OnArchivedAgent_DoesNotLaunch` |

## Edge case review

- **Muted suppression adds to launchedAgentIds** (`RoutingDispatchHandler.cs:88`). Since the unique index prevents `watching`+`muted` coexistence, this is a defensive no-op — the watch pass would never encounter the same agent. Harmless and consistent with "launched/suppressed once" intent.
- **Remove validates active agent** (`WatchEntryStore.cs:110-117`). An archived agent that was previously watching cannot be removed. The watching entry persists but is skipped at dispatch (`LaunchWatchingAgentsAsync:186`). Acceptable per spec ("watch remove SHALL validate the Agent is active").
- **`issueNumber is not > 0` guards** are consistent across muted resolution, `IsMutedForEvent`, `LaunchWatchingAgentsAsync`, and `ResolveIssueRuntimeOverrideAsync`. Matches `TryReadPositiveNumber` semantics (number must be > 0).
- **Cross-delivery source mutation** (config changes between event redeliveries) is explicitly out of scope per D7 and both spec documents. Not covered by tests; tracked as a follow-up.
- **Watch preflight failure does not add to `launchedAgentIds`**, but this is correct: if no routing rule matched the agent, there is nothing to dedup. The rule loop adds to the set on preflight failure so the watch pass skips it.
- **`RecordWatchPreflightFailureAsync` mirrors the routing preflight helper** identically (`RoutingDispatchHandler.cs:273-322` vs `:347-395`) — same grain key construction, same `RoutedAgentLaunchPlan` shape, same terminal-delivery protocol. No divergence risk.

## Non-blocking observations

1. **`IssueWatchEntryDto` includes `CreatedAt`/`UpdatedAt` that neither CLI nor Web render.** The data is projected and available on the wire; future surfaces may use it. Not harmful.
2. **`EventReplay_UnderSameConfiguration_LaunchesAgentOnce` asserts `secondLaunchCount == 2`** (one per delivery). The test name is slightly misleading — the real at-most-once guarantee is grain-level (verified by stable key pairings), not handler-level. The comment explains this clearly.
3. **Web `watching?`/`muted?` typed as `IssueWatchEntry[] | null`.** The server always sends `[]`, not `null`; the optional+nullable type is a defensive choice for forward/backward compatibility. No practical issue.

<promise>PASS</promise>
