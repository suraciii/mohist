# Review: Issue 524

## Result

The provider inbox now persists the route kind and selected Session/Turn in the same transaction as message acceptance. Redelivery reads that fence rather than reconstructing a target from the mutable DM mapping. Launch and New task recovery retain launcher idempotency, while the mapping upsert prevents older message replays from restoring an obsolete current Session.

The current change covers normal follow-up continuity, explicit New task switching, terminal reply identity, queued follow-ups, cancel/stop target isolation, mapping cleanup, route persistence, and route migration. No problems requiring correction before merge were found.

## Verification

- Focused Slack specs passed: 30 / 30.

<promise>PASS</promise>
