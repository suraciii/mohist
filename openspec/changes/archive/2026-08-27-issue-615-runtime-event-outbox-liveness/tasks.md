# Tasks: Runtime Event Outbox Delivery Liveness

> Archived after Issue #763 replaced durable outbox persistence with bounded process-local delivery. Completed tasks below record the historical implementation; the no-concurrent-retry lease is preserved in the volatile queue.

- [x] Add scheduling-group delivery leases and hard-deadline isolation.
- [x] Route late delivery completions through the existing receipt and snapshot
      settlement path.
- [x] Add a focused fake `sendBatch` regression for an unresolved promise that
      ignores cancellation, unrelated-group progress, and late receipt replay.
- [x] Run focused runner typecheck, test, and build evidence.
