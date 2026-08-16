# Runtime Event Outbox Delivery Liveness

## Requirement: An uncooperative delivery cannot hold every scheduling group

The Runtime Event Outbox MUST release its shared drain after a configured
delivery deadline even when the delivery promise ignores `AbortSignal`. It
MUST retain the original durable records and the group's local delivery lease
until that original promise settles.

### Scenario: one group ignores cancellation

- GIVEN one scheduling group has a delivery promise that never resolves and
  ignores cancellation
- AND another scheduling group has a deliverable record
- WHEN the first group's delivery deadline expires
- THEN the first group's record MUST remain in the durable snapshot
- AND the first group MUST NOT be sent again while its original promise is
  unresolved
- AND the second group MUST remain eligible for the normal outbox drain

## Requirement: A late completion preserves receipt identity

The Runtime Event Outbox MUST process a late completion through the same
acknowledgement policy and receipt matcher as an on-time completion.

### Scenario: matching late input receipt

- GIVEN a timed-out Workflow input retains its original delivery lease
- WHEN its original delivery promise later returns a receipt matching that
  input's delivery id, Agent Session id, and Agent Turn id
- THEN the outbox MUST settle that original record once through its atomic
  snapshot path
- AND it MUST NOT send a concurrent retry for that record
- AND it MUST preserve the receipt returned by the original request

### Scenario: non-matching late result

- GIVEN a timed-out record retains its original delivery lease
- WHEN its original delivery promise later fails or returns no matching receipt
- THEN the record MUST remain durable
- AND a later retry MAY begin only after the original lease is released
