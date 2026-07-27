## Findings

### P1: Retired runtime events are not actually rejected at the ingress boundary

The plan treats `TranscriptAccumulator.EventTypes` as the Server's runtime-event authority and says unsupported events therefore cannot be accepted (`design.md:24-26`). T-001 correspondingly tests only that a retired event creates no transcript part (`tasks.json:12`). That is not sufficient for the stated runtime-vocabulary requirement.

`AgentSessionGrain.AppendEventsAsync` applies every submitted event to the domain and creates a `RuntimeEventEnvelope` before the accumulator allowlist is consulted (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:880-929`). Although unsupported entries are skipped for transcript persistence and realtime publishing (`:950-977`, `:1138-1176`), they are still returned to the caller as accepted event info (`:977`, `:1460-1467`), and `ShouldRecordActivity` records activity for every non-input event (`:880-884`, `:980-994`). A retired event can therefore still be accepted observably and refresh Session activity despite producing no transcript part.

The design must choose and specify an ingress behavior before domain application and envelope creation, such as filtering unsupported runtime event types before `ShouldRecordActivity`, `ApplyRuntimeEventToDomain`, return values, and persistence scheduling, or rejecting the request under an explicit API error contract. T-001 must then verify that a retired event neither produces transcript evidence nor event-info/realtime output nor changes Session activity or command eligibility.

<promise>FAIL</promise>
