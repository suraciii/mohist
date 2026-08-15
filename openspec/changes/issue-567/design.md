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

## Interrupt Lease Rollback

The admission fence is a persisted, idempotent lease rather than only the
Runner grain's in-memory `_draining` flag.

1. The CLI generates an opaque interruption id and sends it with
   `POST /api/runner/{runnerId}/update-interrupt`.
2. The Runner grain persists that id as the current pending update fence before
   returning `status=interrupted`. A repeat with the same id returns the same
   fence. A different id cannot replace an active fence; its caller receives
   the existing id and therefore cannot claim it.
3. While a pending id exists, activation and every fresh poll/claim remain
   fenced. Grain activation reconstructs that state, so a Server restart does
   not silently reopen admission.
4. `POST /api/runner/{runnerId}/update-interrupt/{id}/cancel` releases
   admission only when `id` equals the current pending id. It persists the
   released state before reopening admission. Repeating the same cancellation
   is successful and does not mutate work. A delayed repeat of the matching
   `begin` is rejected as already cancelled rather than recreating the fence.
   A stale id is reported as superseded and cannot release a newer fence.
5. A successful Runner registration remains the handoff completion boundary:
   it clears the pending fence durably. A later rollback call for the old id is
   harmless and must not affect a subsequent update.

The managed and plain Runner update paths retain the exact confirmed id. On a
post-confirmation failure, exception, or cancellation they make a best-effort
cancel request with `CancellationToken.None`, after their normal managed
runtime rollback where applicable. A cancellation failure is reported beside
the original update failure; it never turns that failure into success.

This lease does not cause a Runner process to stop, synthesize a result,
redeliver an active work identity, or change Workflow/AgentJob state. It only
prevents a stranded old Runner from remaining permanently unable to claim
after an update transaction fails.

## Non-Goals

- Replay or guess the result of work that was active when the Runner stopped.
- Treat a Runner drain, reconnect, or idle observation as a terminal Workflow
  result.
- Add polling or sleeps to update interruption.
