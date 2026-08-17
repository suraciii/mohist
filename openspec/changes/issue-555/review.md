# Review

This is a re-review of the current change. The issue details were read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; its rendered body is empty, so the acceptance contract was re-read from `proposal.md`, `design.md`, and all five capability specs under this change. Product files were reviewed against those requirements; the files under `openspec/changes/issue-555/` are workflow artifacts.

## Must-fix Findings

### MF-1: Follow-up replay bypasses canonical Project membership

**Where:** `packages/server/src/Mohist.Server/Api/DirectApi/DirectApiRoutes.cs:242-277`, especially the existing-mapping branch at `:246-277`.

The follow-up scope is `(sessionId, Idempotency-Key)`, so a mapping created while the route selected Project A can also be found when a caller selects Project B. The route validates the body and then calls `idempotency.FindAsync` before resolving the canonical Session. When `existing` is non-null, it constructs the claim directly and never calls `sessions.ResolveCanonicalFollowupTargetAsync`; the canonical Project-membership check exists only in the `existing is null` branch at `:251-263`.

A caller whose PAT is authorized for both Projects can therefore reuse a known Session ID and key through Project B and hit Project A's existing mapping. For a completed mapping this can return a rejection observation using the selected Project ID without proving that the Session belongs to it. For a pending mapping it can call `AcceptFollowupAsync` on the canonical Session without a Project-membership check, potentially admitting work into a Session owned by another Project. A different body can also be classified as `idempotency_key_reused` from the other Project's mapping rather than as `session_not_found`.

This violates the `external-agent-caller-auth` requirement that canonical resource Project membership match the selected Project and the `external-write-idempotency`/T-005 acceptance criterion that a Session absent from or not belonging to the authorized Project returns `404 session_not_found` after the grant passes. It also violates the required ordering that resource ownership is checked before the idempotency mapping is used. The replay fix must retain the ability to replay after mutable target invalidation, but it still needs a membership-only canonical check on every request (or an equivalent durable binding check) before consuming the existing mapping; a foreign or missing Session must not reach replay or admission.

## Previous Findings

The four findings from the previous review were checked against the current tree:

- The projection-lag freshness gap for compressed Session lifecycle transitions is fixed. `PublicExecutionReadQuerier.AddSessionFeedsAsync` now compares the `AgentSessionLifecycle` head at `:291-326`, and `PublicExecutionProjectionSpecs.LifecycleHistoryHead_MakesACompressedCycleReadAsProjectionLag` covers the regression.
- Retryable queued dispatch states now expose `queue_full` as both a safe reason and error. `PublicExecutionAggregator` sets these fields at `:218-222`, `:264-266`, `:304-306`, and `:347-349`, with the public projection regression covered by `RetryableDispatchBlock_ProjectsSafeQueueFullReasonAndError`.
- A matching retry while a stop remains unresolved now returns `stop_pending` at `DirectApiStopRoutes.cs:127-133` instead of treating a current public snapshot as a completed command. The stop spec asserts the `503` response and no replacement delivery.
- Replay after an Agent archive and after follow-up target invalidation now finds the durable mapping before the mutable write-target lookup. The launch path does this at `DirectApiRoutes.cs:481-508`, and the focused replay specs cover both intended recovery cases. That fix is behaviorally correct for those cases, but its follow-up branch introduced the Project-membership bypass reported above.

The earlier lifecycle-history compression and deleted-Session tombstone findings were also rechecked: lifecycle transitions are persisted by `AgentSessionStore`, and deletion closes the public stream while purge removes the tombstone only through the retention operation. No additional must-fix regression was found there.

## Dimension Checks

- **Issue contract and acceptance criteria:** FAIL. The direct boundary, projection, reads, keyed writes, stop fencing, event cursors, and shipped documentation are present, but MF-1 violates the follow-up ownership and replay criteria.
- **Coverage:** FAIL. The current suites cover follow-up replay after target invalidation, but no test exercises an existing `(sessionId, key)` mapping replayed through a different authorized Project. That case can admit or expose the wrong Project's mapping.
- **Correctness:** FAIL for MF-1. The valid replay path and the cross-Project replay path are indistinguishable once `FindAsync` returns a row because the route skips canonical ownership resolution.
- **Consistency with the surrounding codebase:** checked, no additional issue found. The middleware, projection, persistence, public serialization, and canonical stop composition follow the local conventions; the ownership omission is described above.
- **Tests and verification:** checked. `npm run test:fast` passed all seven lanes, the focused follow-up suite passed all 8 tests, and `npm run verify` passed docs, file-size, format, build, 3,984 Server SpecTests, 2,676 Server unit tests, 1,848 CLI tests, 4,724 Web tests, 1,639 Runner tests, and 70 Slack tests. The green suite does not cover MF-1's cross-Project replay scenario.

## Observations

- `20260909000000_AddPublicApiCursorSecret.cs` rebuilds the existing `StoredSecrets` table to extend its check constraints for the persisted cursor key. It copies existing rows, but this migration deserves deployment testing against populated secret stores. This does not add another must-fix finding here.
- The queued projection maps several retryable internal wait reasons (`capacity-full`, `concurrency-limit`, and `no-online-runner`) to the single safe public reason `queue_full`. That is consistent with the current public vocabulary and is recorded only as an implementation detail, not a release blocker.

## Verdict

**FAIL** — MF-1 remains a must-fix ownership and idempotency correctness problem relative to the issue acceptance criteria.

<promise>FAIL</promise>
