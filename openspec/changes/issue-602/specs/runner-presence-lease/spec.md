## Requirement: Persist an absolute lease

The Runner grain MUST persist `PresenceLeaseExpiresAt` as absolute UTC `DateTimeOffset?` at Orleans field ID `6`. The existing presence timeout MUST remain exactly two minutes. The persisted lease, not `_lastPresenceAt` or `RunnerInfo.RegisteredAt`, is authoritative.

### Scenario: Registration and real presence renew a lease

- **WHEN** registration, heartbeat repair, heartbeat, or successful poll completes at `now`
- **THEN** the Runner MUST persist `now + 2 minutes` before or atomically with online registry publication.

### Scenario: Activation preserves a future lease

- **WHEN** activation reads a persisted expiry later than the injected current time
- **THEN** it MUST project the Runner online from the persisted profile and supervise only the remaining duration; it MUST NOT mint a new lease.

### Scenario: Activation converges an elapsed lease

- **WHEN** activation reads an expiry at or before the injected current time
- **THEN** it MUST clear the lease, persist offline state, unregister the volatile index, and use the existing generation closeout backstop without renewing presence.

### Scenario: Legacy state remains offline

- **WHEN** old state has no field ID `6`
- **THEN** activation MUST remain offline and MUST NOT invent a lease or registry eligibility.

### Scenario: Explicit unregister clears presence

- **WHEN** a registered Runner unregisters
- **THEN** it MUST clear and persist the lease before removing registry membership, and later activation MUST NOT resurrect it from old state.

## Requirement: Reminder and eligibility are durable boundaries

The existing `presence` reminder MUST supervise persisted expiry across activation. The existing ten-second grain timer MAY remain only as a low-latency optimization. `ListEligibleRunnersAsync` MUST ask `IsPresenceLeaseActiveAsync` for every indexed Runner and fail closed when the authority returns false or cannot be read. `ListAllAsync` MUST remain diagnostic and unfiltered.
