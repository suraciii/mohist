## Implementation

- [x] Audit the current managed runtime transaction and the historical #576
  patch against current master.
- [x] Add transaction-owned stable CLI launcher activation, finalize, and
  rollback behavior.
- [x] Verify candidate source revision through the stable launcher.
- [x] Make an explicit existing absolute `--cli-path` the activated and
  verified entrypoint; reject missing or relative paths before staging.
- [x] Document and surface the reachable source-checkout bootstrap instead of
  relying on a pre-change legacy `mo update cli` path.
- [x] Add focused migration, idempotence, verification rejection, and commit
  rollback tests, including explicit-path failure before mutation.

## Validation

- [x] Run the focused CLI test classes twice and inspect their apphost counters.
- [x] Build the CLI test project without invoking a managed update or Runner.
