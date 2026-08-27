## Why

Runner presence currently depends on in-memory freshness, so a grain reactivation can lose the two-minute expiry boundary. Runner-loss closeout also clears its durable generation obligation after a one-shot attempt, so an owner delivery exception can leave work waiting for a slower fallback.

## What Changes

- Persist the absolute UTC presence lease in `RunnerState` using optional Orleans field ID `6`.
- Register the existing `presence` reminder and rebuild supervision from the persisted lease on activation.
- Make registry eligibility ask the Runner grain's lease authority and fail closed for missing, expired, offline, or unavailable state.
- Keep `ClosingProcessGeneration` as the one durable Server-side closeout obligation introduced by the generation-fenced Runner design. Retry both owner scans and all matching owner deliveries on reminder and activation until no query, delivery, or outstanding verdict remains.
- Preserve #766 process-generation fences and owner `Accepted`/`Refused` verdict semantics. Do not add a per-owner ledger, owner deadline matrix, interruption/Unknown API, generic queue, or Runner-process journal.

## Non-Goals

No change to the two-minute lease, ten-second low-latency cadence, poll protocol, capacity, owner recovery policy, process-generation contract, or CI topology.
