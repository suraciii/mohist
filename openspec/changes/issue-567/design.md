# Design: Managed Runner Update Recovery Boundary

## Sequence

For managed `runner` and `full` updates:

1. Resolve the current Runner identity from the authoritative hostname lookup.
2. Build and validate the immutable candidate and capture the current service
   snapshot.
3. POST the identity-bound update interrupt. Accept it only when the response
   has the same `runnerId`, `status=interrupted`, and a consistent work-id
   count.
4. Atomically write the transaction and active target, then activate and verify
   the candidate as before.

The precondition is invoked after candidate construction but before any active
pointer or candidate service-unit change. A normal unconfirmed result never
activates the candidate or restarts the service. If the callback throws after
it has been invoked, the existing snapshot restore path restores the prior
service state and the candidate is still not activated.

## Staging Cleanup

Moving the candidate into `releases/<release-id>-g<generation>` is staging, not
activation. If the transaction exits before `active.json` is written, the
transaction removes that exact release root. Once `active.json` is written,
the existing durable rollback/fail-closed path owns the release and no cleanup
is inferred.

## Non-Goals

- Replay or guess the result of work that was active when the Runner stopped.
- Treat a Runner drain, reconnect, or idle observation as a terminal Workflow
  result.
- Add polling or sleeps to update interruption.
