# Self Review: Issue 522 Plan (round 3)

## Findings

No blocking findings. The round-2 regression is resolved.

Round-2 finding 1 (D8's activity-driven terminal clobbering the launch Turn's authoritative terminal result) is fixed: D8's terminal marking now skips any Turn whose `JobId` is set, and the design accurately explains why the guard is required — `AppendTerminalCloseAsync` ingests its `session.activity` through the shared `AppendEventsAsync → ApplyRuntimeEventToDomain` path (`AgentSessionGrain.cs:833`) and `EnterTerminalStateAsync` runs it at `AgentJobGrain.cs:1323` before the authoritative `MarkInitialTurnTerminal` at `:1324`. The `JobId` discriminator is sound: launch Turns carry a `JobId` (from `EnsureInitialLaunchCommand.JobId`) while D1's follow-up Turns do not — including follow-up Turns on a launch-sourced session, since an AgentJob owns only the first Turn. T-001's lifecycle criterion and test coverage now assert launch-Turn isolation on both the Executing and terminal-close paths, so the guard is testably enforced.

Cross-checks that confirm the guard is consistent with the rest of the plan:
- Stop of a launch Turn is adjudicated by the AgentJob (result/Unknown); D8 skips its `session.activity`, so the launch Turn's terminal stays Job-driven.
- Stop/terminal of a follow-up Turn (no `JobId`) is driven by D8 from its `session.activity`, so it is not left in `Executing`.
- The `Queued → Executing` promotion never fires for a launch Turn (issue-512 removed its Runner `session.input`), so the guard is only load-bearing on the terminal path, as designed.

Earlier rounds' findings remain resolved: follow-up Turn lifecycle is specified (D8) and tasked (T-001); D5's stale-guard enumerates only real `AgentTurnStatus` values; T-004 requires updating `design/cli.md` and `docs/cli-reference.md`. The follow-up-cancel best-effort limitation (synchronous delivery cannot be un-delivered) and its activity-idle consequence are documented in D8 as accepted tradeoffs, not hidden gaps. The one Open Question (`stop-requested` as persistent status vs. reply label) is bounded and does not block build.

<promise>PASS</promise>
