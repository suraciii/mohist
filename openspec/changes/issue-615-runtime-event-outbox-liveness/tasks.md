# Tasks: Runtime Event Outbox Delivery Liveness

- [x] Add scheduling-group delivery leases and hard-deadline isolation.
- [x] Route late delivery completions through the existing receipt and snapshot
      settlement path.
- [x] Add a focused fake `sendBatch` regression for an unresolved promise that
      ignores cancellation, unrelated-group progress, and late receipt replay.
- [x] Run focused runner typecheck, test, and build evidence.
