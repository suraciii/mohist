# Review - Issue 470

81 files changed, +7466/-511 lines across all 8 tasks. All tasks are committed and
unit tests (1318) pass. This review covered the observability publication core,
OTLP ingestion and response wire contracts, tri-state status, request accounting
and feedback isolation, ranked route diagnostics, host-runner fallback, and
agent-path amplification.

## Findings

### F1 - TOCTOU between `RecordAgentPath` and `Dispose()` (fixed)

`RuntimeObservability.RecordAgentPath` previously checked `IsDisposed()` outside
the `_gate` lock and then issued three `_path*.Record(...)` instrument calls also
outside the lock with no re-guard. `Dispose()` could run on another thread between
the check and the records, dispose the `Meter`, and throw from the instruments.

Fix: the three `Record` calls now run inside `lock (_gate)` with an in-lock
`_disposed` re-check, matching the pattern already used by `CompleteRequest`.
Covered by `RecordAgentPathAfterDisposeDoesNotThrow`.

## Coverage And Structure

- The eight tasks land a fixed low-cardinality `Meter` catalog, source-owned
  degradation state machine, bounded five-minute route ring, non-initializing
  storage probe, sole process/storage sampler, tri-state status DTO/API/CLI,
  request-local work accounting with EF/Orleans/HttpClient adapters, scope-suppressing
  background launcher and OTel feedback isolation, `MohistHostRunner` bind-failure
  fallback with deterministic ordered-seed `latest_degradation`, and project-scoped
  agent amplification counts with explicit-project compatibility routes.
- `Mohist.Server.UnitTests`: 1318 passed, 0 failed after the fix; the focused
  Telemetry/HostLifecycle slice (110 tests) is green.

<promise>PASS</promise>
