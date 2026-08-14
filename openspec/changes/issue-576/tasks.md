## Implementation

- [x] Audit the current managed runtime transaction and the historical #576
  patch against current master.
- [x] Add transaction-owned stable CLI launcher activation, finalize, and
  rollback behavior.
- [x] Verify candidate source revision through the stable launcher.
- [x] Add focused migration, idempotence, verification rejection, and commit
  rollback tests.

## Validation

- [x] Run the focused CLI test classes twice and inspect their TRX counters.
- [x] Build the CLI test project without invoking a managed update or Runner.
