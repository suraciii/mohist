# Review: issue-521

## Findings

### F1. An acknowledged delivery with no runtime event makes a queued turn permanently undispatchable

`AgentSessionFollowupDispatcher` retains `Dispatching = true` whenever the runner returns `{ accepted: true }` ([AgentSessionFollowupDispatcher.cs:78](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:78)). Until the runner emits `session.input`, the turn remains `queued`, but `BeginNextFollowupDispatchAsync` refuses a lease with that flag ([AgentSessionGrain.cs:503](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:503)).

If the runner acknowledges the SignalR call and then crashes or loses its outbox event, a retry with the same idempotency key returns the original queued input but cannot re-attempt delivery. This violates D9 and the input spec's queued-turn retry requirement. Represent the delivery state so an unconfirmed dispatch can be retried safely, or release/reclaim the reservation under an explicit retry rule; add a test for accepted SignalR delivery followed by no `session.input` and a same-key retry.

### F2. The dispatch reservation splits inputs that are still assigned while a turn is queued

The grain marks a turn `Dispatching` before the runtime has emitted the event that changes its status to `executing` ([AgentSessionGrain.cs:509](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:509)). A follow-up accepted during that interval skips the still-`queued` turn solely because of this flag ([AgentSession.Transitions.cs:812](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:812)) and creates a new turn, while the first dispatch has already snapshotted its prompt text ([AgentSessionFollowupDispatcher.cs:73](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:73)).

This violates the planned turn-assignment rule: inputs accepted while a turn is queued must join its `InputIds` in submission order. Make dispatch claim and queue admission consistent, for example by explicitly transitioning the claimed turn out of the joinable queued state or by ensuring the dispatch payload includes inputs admitted before the execution boundary. Cover the race through the HTTP/runner boundary, not only direct grain calls.

### F3. Issue-session Web pages do not refresh follow-up observation after an idle session begins executing

`useIssueSessionDataSource` records the follow-up result but never invalidates its `metadataQueryKey` ([useIssueSessionDataSource.tsx:255](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:255)). The realtime invalidation added to `useSessionTranscript` only exists while `isRunning` is true; an initially idle session does not subscribe to `session.input` ([useSessionTranscript.ts:223](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:223), [useSessionTranscript.ts:394](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:394)). The page therefore keeps the accepted-result fallback of `queued` even after the server marks the turn executing ([useIssueSessionDataSource.tsx:275](/home/szf/.mohist/projects/workspaces/wr_523a0d94be64409990d59c564f3f550a/packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:275)).

Invalidate/refetch the issue session observation on follow-up acceptance and ensure the event subscription can observe the transition from idle to active. Add a page-level test that starts idle, submits a follow-up, receives `session.input`, and asserts the status changes from accepted-pending to executing.

<promise>FAIL</promise>
