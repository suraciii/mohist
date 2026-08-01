# Review — Issue 516 (thread discussion as agent startup context)

Re-review after the fix round (commits `096939e50`, `57e3496ea`, `a54189d7e`,
`82fcc26e6`). This review judges the change as it stands now against the issue's
acceptance criteria and the plan artifacts. The prior review's five findings
(F1–F5) are each checked below; no new problems found.

## Verdict

**PASS** — the blocker (F1) and all other findings are resolved correctly, no
regressions or new issues were introduced, and the full server suite is green.

## Prior findings — resolution check

| Prior finding | Status | Evidence |
|---|---|---|
| F1 BLOCKER — reader discarded prior messages on mention hit | ✅ Fixed | `SlackThreadHistoryReader.ReadAsync` (`SlackThreadHistoryReader.cs:94-106`) now collects messages with `ts < mentionTs` and stops at the first `ts >= mentionTs`, returning `Imported(collected)` instead of `Empty(...)`. The unit test `ReadAsync_KeepsPriorMessages_StopsAtMention` asserts the prior message survives, and the spec fake pages now include the mention message (mirrors real Slack). |
| F2 — depth-cap exhaustion imported oldest slice | ✅ Fixed | `SlackThreadHistoryReader.cs:72,108-121` tracks `reachedMention` and `paginationComplete` separately; when the cap is exhausted without reaching the mention or end-of-pagination it returns `Refused("pagination depth cap reached before the mention")`. Unit test `PaginationDepthCap_RefusesWhenMentionBeyondCap` covers it. |
| F3 — dead refuse-path `ReleaseAsync` | ✅ Fixed | `SlackConnectionRoutes.ReadThreadHistoryIfAnyAsync` (`SlackConnectionRoutes.cs:410-423`) is now a clean passthrough; the unused `SlackThreadLaunchReservationStore.ReleaseAsync` method was removed. The reservation is created later inside `LaunchChannelRootAsync`, so the refuse path correctly has nothing to clean up. Misleading test names renamed. |
| F4 — post-mention messages leaked when mention absent | ✅ Fixed | The timestamp boundary (`ts < mentionTs`) excludes any message at or after the mention regardless of whether the mention itself is present. Unit test `MentionExcluded_PostMentionMessagesDropped` covers it. |
| F5 — indentation regression in AgentSessionGrain.cs | ✅ Fixed | `AgentSessionGrain.cs:2179-2192` restored to method-body indentation. |

## Acceptance criteria check

| AC | Status | Where |
|---|---|---|
| 1 — visible scope + mention is the task | ✅ | Acceptance reply states "Prior thread discussion is being used as background" (`BuildLaunchAck`, `SlackConnectionRoutes.cs:1622-1632`); mention text minus the bot mention is the task (`RemoveBotMention` → `prompt`). |
| 2 — stable truncation, marked in reply + agent input | ✅ | Oldest-first drop in `ApplyBudget`; marker "N oldest messages omitted" flows to both the reply (`BuildLaunchAck`) and the agent input (`AgentStartupContextComposer.RenderBackground`). Spec `OverBudget_TruncatesOldestFirst_AndDualMarkedInReplyAndInput` asserts both. |
| 3 — incomplete read → no AgentJob | ✅ | Refuse path replies and returns before `LaunchChannelRootAsync` (`SlackConnectionRoutes.cs:1224-1229`); depth-cap exhaustion now also refuses. |
| 4 — empty mention → no work | ✅ | Empty-mention check precedes the read (`SlackConnectionRoutes.cs:1216-1221`). |
| 5 — history as untrusted input | ✅ | Composed as a read-only background block prepended to the prompt (`AgentJobGrain.cs:1105-1106` → `AgentStartupContextComposer`); Instructions/Runtime/Model/Variant/Skills are separate dispatch fields, verified unchanged by `AgentStartupContextLaunchSpecs`. |
| 6 — edits/deletes immutable | ✅ | No edit/delete handler exists; accepted input persists in grain state and is not reactive to later Slack mutations. |

## Notes (non-blocking)

- `ApplyBudget`'s `OrderBy` key selector calls `TryReadMessageTs` and ignores its
  Boolean return, relying on the preceding `Where` to have already excluded
  unparseable timestamps (`SlackThreadHistoryReader.cs:140-147`). This is correct
  (LINQ evaluates `Where` before the key selector for each element) and harmless;
  noted only because a future refactor that drops the `Where` would silently sort
  on `0`. Does not block merge.
- Timestamp comparison uses `double` (`TryReadMessageTs`). At the current Slack
  epoch (~1.7e9) with 6-digit microsecond precision (14 significant figures) this
  is well within `double`'s 15–17 significant-digit capacity, so ordering is
  exact. Would only become a concern at far-future epochs; not actionable now.

## Verification

- `npm run build` — green, 0 warnings, 0 errors.
- `npm test` — Workflow.Definition 175/175, Cli 1487/1487, Server.Unit 1717/1717,
  ArchTests 51/51, SpecTests 3585/3585 (clean run, no flakes).

<promise>PASS</promise>
