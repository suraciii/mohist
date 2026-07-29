# Review: issue-521

## Findings

### F1. Queued follow-up turns are dispatched immediately and never scheduled after the active turn ends

`AgentSessionFollowupRoutes.ExecuteFollowupAsync` sends one `ReceiveFollowup`
call for every accepted HTTP request, using that request's individual `text`
([AgentSessionFollowupRoutes.cs:191](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:191),
[AgentSessionFollowupRoutes.cs:196](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:196)). This also occurs when `AcceptFollowup` assigns the input to a new queued turn behind an executing turn, or joins it to an existing queued turn. It therefore sends work that should wait behind the current execution, and sends separately the inputs that the domain deliberately joins into one turn.

When the active turn reaches terminal state, `MarkFollowupTurnTerminal` only records that terminal state and removes its lease; it does not identify and dispatch the next queued turn ([AgentSession.Transitions.cs:855](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:855)). A queued turn can consequently either run concurrently/early, or remain queued forever. Implement turn-level dispatch: dispatch only the eligible head turn, combine its ordered `InputIds` into its runner payload, and dispatch the next queued turn after the previous one terminates. Add an API/lifecycle spec that submits inputs during execution, terminalizes the active turn, and verifies the next turn is dispatched exactly once with its ordered inputs.

### F2. Web cannot submit a follow-up while another follow-up is pending or executing, and does not reliably update to executing

The composer derives `isQueued` from an accepted queued result or its local queued state and disables the textarea whenever it is true ([SessionFollowupComposer.tsx:54](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/widgets/coder-session/ui/SessionFollowupComposer.tsx:54),
[SessionFollowupComposer.tsx:164](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/widgets/coder-session/ui/SessionFollowupComposer.tsx:164)). This directly prevents the product-required flow of adding an input while a turn is in flight.

Further, the displayed `turnStatus` comes from the cached summary/metadata and falls back to `queued` ([useGenericSessionDataSource.ts:155](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/pages/session/data/useGenericSessionDataSource.ts:155),
[useIssueSessionDataSource.tsx:266](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:266)). The `session.input` realtime handler only appends transcript state and does not invalidate/refetch the observation that carries `turns` ([useSessionTranscript.ts:380](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:380)). The composer can thus remain in the disabled “Accepted - pending” state after the runner starts execution. Keep the composer usable for additional inputs except during its own submission/unavailable session state, and refresh or update the observation on turn lifecycle events so queued and executing are accurately distinguished. Cover both flows in the Web tests.

### F3. CLI always reports an accepted follow-up as queued and never observes the turn

`mo session followup` only posts the follow-up response; it does not read the session observation ([MohistCliCommands.Session.cs:274](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/cli/Mohist.Cli/MohistCliCommands.Session.cs:274)). The response shape has no `turnStatus`, yet the table renderer fabricates `queued` whenever an accepted response omits it ([TableRenderer.Entities.cs:471](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/cli/Mohist.Cli/TableRenderer.Entities.cs:471)). An accepted call whose turn is already executing is therefore displayed as pending, which violates the shared Web/CLI status interpretation criterion.

Obtain the corresponding input/turn status from the session observation (or return an authoritative status in the accepted response) and render that value without a fabricated queued fallback. Add CLI coverage for both queued and executing observations.

## Verification

- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,445 tests.
- `npm run typecheck -w packages/web` passed. The subsequent Web test command did not complete before the command timeout and had three `App.test.tsx` failures in its partial output.
- The attempted Server test filter is ignored by Microsoft.Testing.Platform; the resulting full suite ran 3,354 tests with one failure. The binary test log did not expose the failing test name through the available reader.

<promise>FAIL</promise>
