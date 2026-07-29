# Review: Issue 522

## Findings

### Critical: Stop completion facts are not correlated to their target Turn

`packages/runner/src/server/cancel-handler.ts:127` calls `recordCancelActivity` without the request's `payload.turnId`, and the emitted `session.activity` payload at `:160-168` consequently has no `turnId`. On the Server, `DriveTerminalActivityLifecycle` falls back to `MarkCurrentNonLaunchTurnTerminal` for such an event (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1604-1607`). A delayed stop result for follow-up Turn A can therefore mark a later executing follow-up Turn B terminal or Unknown. This violates the required per-Turn stale guard and can stop unrelated newer work.

Forward the stop request's Turn ID in the Runner outbox fact and reject stale/non-current terminal activity before changing Session activity. At present, even a correctly targeted stale event would first call `SetActivity` at `AgentSessionGrain.cs:1583-1585`, incorrectly changing B's Session activity to idle or unknown. Add Runner and Server regression coverage for a delayed A stop fact arriving after B has started.

### High: An unconfirmed stop of the launch Turn leaves the Turn executing

The stop route returns the Runner's `unknown` reply directly (`packages/server/src/Mohist.Server/Api/AgentSessionStopRoutes.cs:112-132`) but never calls the owning `IAgentJobGrain.MarkUnknownAsync`. The Runner only emits `session.activity` (`packages/runner/src/server/cancel-handler.ts:151-168`), while Server lifecycle handling intentionally skips any Turn with a `JobId` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1593-1602`). Thus an unconfirmed Pi stop makes Session activity Unknown but leaves the initial Turn `Executing` and its AgentJob `Running`, contrary to the stop spec's requirement that both target Turn and Session remain Unknown.

Route an unconfirmed launch-Turn stop through the owning AgentJob's existing Unknown transition so it propagates the authoritative Unknown result to the initial Turn. Extend `GenericAgentSessionCancelApiSpecs` to assert persisted Turn and Job state, not only the HTTP reply.

### High: Web controls send transcript presentation IDs instead of durable AgentTurn IDs

Both Web data sources obtain `currentTurnId` from the final transcript/display turn (`packages/web/src/pages/session/data/useGenericSessionDataSource.ts:148` and `packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:252`). Those IDs are not `AgentTurnRecord.Id`: `SessionTranscriptBuilder` fabricates `turn-{sequence}` at `packages/server/src/Mohist.Server/Sessions/Services/SessionTranscriptBuilder.cs:20-24`, and live turns are generated as `live-*` at `packages/web/src/widgets/session-transcript/model/transcript-state.ts:32-56`. The cancel and stop endpoints resolve only persisted Turn IDs (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:710-723`). Consequently, the normal Web control sends an ID that the Server rejects as not found rather than controlling the active Turn.

Expose the current durable Turn ID through the Session read model/control data and use that value for the Web mutation. Add an integration-level Web test that distinguishes a transcript display ID from the durable Turn ID and verifies the request uses the latter.

### High: CLI cannot discover a follow-up Turn ID needed by its new controls

`mo session cancel` and `mo session stop` now require `--turn-id` (`packages/cli/Mohist.Cli/MohistCliCommands.Session.cs:318-369`), but the CLI's follow-up response cannot supply it. Server follow-up success returns only `AgentSessionFollowupResult(target.SessionId)` (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:222-227`); its type has only `SessionId` and `Status` (`:257`). Neither the Session read/transcript route exposes the persisted `AgentTurnRecord.Id`; transcript IDs are fabricated (`packages/server/src/Mohist.Server/Sessions/Services/SessionTranscriptBuilder.cs:20-24`). A user who starts a follow-up Turn therefore has no CLI-visible target to pass to either control command, despite the CLI reference promising that follow-up returns the Turn ID.

Return the durable `inputId` and `turnId` for an idle-start follow-up, and/or add a Session read projection that exposes addressable current Turn IDs. Update the CLI contract tests to cover discovering a follow-up Turn and then controlling that exact returned ID.

## Verification

Targeted Runner, Web, and Server test commands were started but interrupted by the workflow request before they completed; no passing test result is claimed here.

<promise>FAIL</promise>
