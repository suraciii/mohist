## Findings

### P1: The required full test suite is still flaky

`npm test` fails in `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionEventDiscardObservabilitySpecs.cs:108`, where `MixedBatch_DiscardsRetiredTypesAndProcessesSupportedEvents` expects a transcript flush containing `session.activity`. The failure recorded by this change has only an unrelated flush, so `Assert.Single(..., predicate)` finds no matching item. This file was changed by T-001 specifically to handle concurrent unrelated flushes, but the current synchronization still permits the assertion to run before the target flush arrives. Make the test wait on a deterministic signal for the target transcript flush, rather than only waiting for the persistence checkpoint, so `npm test` satisfies the issue's acceptance criteria reliably.

## Verification

- Focused server unit suite: 1541 passed.
- Focused server spec suite: 3279 passed.
- `npm run build` passed.
- `npm test` failed in the spec above.

<promise>FAIL</promise>
