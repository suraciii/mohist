# Review: Issue 471

## Findings

### P2: The admission-before-body-read requirement has no effective regression test

`OtlpIngestGate.BlockNextRequestLease` only creates `_nextRequestSignal`, but `TryAcquireRequestLease` never observes or awaits that signal ([OtlpIngestGate.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/src/Mohist.Server/Otel/OtlpIngestGate.cs:39)). Consequently the seam used by the over-limit specs is inert. The tests named `FifthRequest_RejectedWithoutReadingBody` and `FourthAdmit_ProvisionalSixthRefusedBeforeBodyRead` submit `StringContent` and assert only the response/runtime counters ([OtlpRoutesBoundedIngressSpecs.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/OtlpRoutesBoundedIngressSpecs.cs:130), [OtlpRoutesBoundedIngressSpecs.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/OtlpRoutesBoundedIngressSpecs.cs:286)). They cannot observe whether the server read the rejected HTTP body.

Add a controlled request-content/body stream to the TestServer route fixture that signals or fails on its first read, fill the four gate slots, and assert the fifth recognized JSON and protobuf request returns `429` without that signal firing. Remove the unused `BlockNextRequestLease` seam or make it a real, observable admission test seam. This is required by the issue and task acceptance criteria, which explicitly require deterministic first-read ordering coverage without scheduler or wall-clock waits.

<promise>FAIL</promise>
