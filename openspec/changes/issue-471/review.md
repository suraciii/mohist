# Review: Issue 471

## Findings

### P1: Writer gate can grant one lease to multiple requests

`OtlpIngestGate.AcquireWriterLeaseAsync` stores every contending request against the same `_writerSignal` task ([OtlpIngestGate.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/src/Mohist.Server/Otel/OtlpIngestGate.cs:68)). When the active writer releases, `ReleaseWriterLease` clears `_writerLeaseInUse` and completes that one shared task ([OtlpIngestGate.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/src/Mohist.Server/Otel/OtlpIngestGate.cs:103)). Every waiter then returns `new OtlpWriterLease(owner)` without reacquiring under the lock ([OtlpIngestGate.cs](/home/szf/.mohist/projects/workspaces/wr_c472561281e24c5890f94ec4dadce1e2/packages/server/src/Mohist.Server/Otel/OtlpIngestGate.cs:85)).

With three admitted requests waiting behind one writer, a single release wakes all three and all enter `TraceIngester.CommitBlock` concurrently, violating the requirement that at most one admitted request execute database writes. A newly arriving request can also acquire in the interval after `_writerLeaseInUse` is cleared and before the signalled waiter resumes. Later disposal of multiple granted leases throws because only one lease was actually represented by `_writerLeaseInUse`. Cancellation of any waiter also cancels the shared signal and therefore cancels every other waiter.

Replace the shared broadcast signal with a cancellation-safe one-at-a-time handoff, or make every awakened waiter re-contend under the lock. Add a deterministic contention test that holds one writer, queues at least two admitted writers, releases the holder, and proves that exactly one transaction is active at every point. The current `AcceptedRequest_SerializedByGate_NoTwoWritersAtOnce` test sends only one request and cannot verify the acceptance criterion.

## Verification

`dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~Telemetry'` passed (the test platform ignored the filter and ran 1506 unit tests).

`dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter 'FullyQualifiedName~Telemetry'` ran 3246 specs and failed one documented pre-existing `EventDispatcherImmediateTriggerSpecs.LostImmediateTrigger_LeavesRowUndispatched_AndReminderTickRecovers` flake; the issue artifact records the same failure as unrelated to this change.

<promise>FAIL</promise>
