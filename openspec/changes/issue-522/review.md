# Review: Issue 522

## Findings

### High: A non-launch Turn made Unknown by stop can never reconcile on later Runtime evidence

The new synchronous unknown transition in `AgentSessionStopRoutes.ExecuteStopAsync` marks a follow-up Turn `Unknown` (`packages/server/src/Mohist.Server/Api/AgentSessionStopRoutes.cs:134-137`). A later authoritative `session.activity` fact with that same `turnId` then enters `DriveTerminalActivityLifecycle`, but its `FindCurrentNonLaunchTurn` lookup excludes `Unknown` Turns (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1592-1606`, `:1639-1654`) and returns without applying the completed/failed observation. This is not an immutable-domain-state restriction: `MarkTurnTerminal` explicitly permits `Unknown → Completed|Failed` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:617-647`), but the runtime-fact path cannot reach it.

An unconfirmed Pi stop can therefore leave a follow-up Turn and its Session permanently Unknown even after the Runtime later reports completion or failure. That violates the stop spec's requirement that Unknown reconcile on authoritative Runtime evidence. Preserve the stale/current guard, but allow a terminal fact correlated to the same current Unknown non-launch Turn to call `MarkTurnTerminal`; add a lifecycle/API regression for `Executing → Unknown` from an unconfirmed stop followed by a correlated completed and failed Runtime activity fact.

## Verification

The current branch's recorded validation shows full .NET, Runner, and Web suites passing. This review traced the post-stop correlated Runtime activity path and found the missing Unknown reconciliation transition.

<promise>FAIL</promise>
