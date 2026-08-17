# Review

This is a re-review. The issue details were read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; its rendered body is empty, so the acceptance contract was checked against `proposal.md`, `design.md`, and all five capability specs. The prior review's product findings were verified against the current tree. Only `review.md` changed after that review; no product fix has addressed the remaining finding.

## Must-fix Findings

### MF-1: Follow-up replay bypasses canonical Project membership

**Where:** `packages/server/src/Mohist.Server/Api/DirectApi/DirectApiRoutes.cs:246-277`, especially the existing-mapping branch at `:246-250` and `:271-277`.

Follow-up mappings are scoped by `sessionId|Idempotency-Key`, without `projectId`. The route validates the body and calls `idempotency.FindAsync` before it calls `sessions.ResolveCanonicalFollowupTargetAsync`. When a mapping already exists, the route constructs the claim directly and never performs the canonical Session lookup or Project-membership check.

For example, after a mapping for a Session in Project A exists, a PAT authorized for Project B can submit that Session ID and the same key through Project B. With the same body, the request enters replay; with a different body, it receives `409 idempotency_key_reused` based on Project A's mapping. If the mapping is still pending, the route calls `AcceptFollowupAsync` on the canonical Session without verifying that it belongs to the selected Project. These outcomes are possible even though the selected Project passed the middleware grant check.

This violates the `external-agent-caller-auth` requirement **Project authorization precedes resource lookup**, which requires canonical resource Project membership to match the selected Project. It violates the `external-write-idempotency` requirement **Durable keyed mappings are scoped per command** and its follow-up requirement that a Session absent from or not belonging to the authorized Project returns `404 session_not_found`. It also violates T-005 acceptance criterion 1 in `tasks.json`, and the design's required ordering that ownership is checked before idempotency is used.

The fix must preserve replay after mutable target invalidation, which is covered by `DirectApiFollowupSpecs.CompletedFollowupReplaySurvivesSessionTargetInvalidation`. Add an existence-and-membership-only canonical Session check on every request before reading the mapping, or an equivalent check that cannot be bypassed by replay. Do not require the mutable Runner/source target to remain valid for an existing mapping, but a missing or foreign Session must return `404 session_not_found` and must never reach replay or admission.

## Previous Findings

The prior findings were checked against the current unchanged product tree:

- The compressed Session lifecycle projection-lag gap remains fixed. `PublicExecutionReadQuerier` compares the lifecycle head, with regression coverage in `PublicExecutionProjectionSpecs.LifecycleHistoryHead_MakesACompressedCycleReadAsProjectionLag`.
- Retryable queued dispatch states still project the safe `queue_full` reason and error, with the corresponding projection regression test.
- A matching stop retry while a stop is unresolved still returns `stop_pending` and does not create a replacement delivery.
- Launch replay after Agent archival and follow-up replay after mutable target invalidation still load the durable mapping before the mutable operation lookup.
- Lifecycle-history persistence and deleted-Session public-stream tombstone behavior remain covered; no regression was introduced.

MF-1 is not fixed: the follow-up route's mapping lookup still precedes its canonical Project-membership check.

## Dimension Checks

- **Issue contract and acceptance criteria:** FAIL. MF-1 violates the follow-up ownership and idempotency ordering criteria above.
- **Coverage:** FAIL. `DirectApiFollowupSpecs` covers a missing/foreign Session before any mapping exists and replay after target invalidation, but no test covers an existing `(sessionId, key)` mapping submitted through a different selected Project.
- **Correctness:** FAIL for MF-1. Existing mappings can be classified or admitted without proving that the canonical Session belongs to the selected Project.
- **Consistency with the surrounding codebase:** checked, no additional issue found. The direct middleware, public projection/read boundary, and canonical stop composition remain consistent; the ownership omission is the exception described above.
- **Tests and verification:** checked. The recorded full `npm run verify` and focused suites passed in the prior review state, but the green suite does not exercise MF-1's cross-Project replay case. No product files changed after that verification.

## Observations

- `20260909000000_AddPublicApiCursorSecret.cs` rebuilds the existing `StoredSecrets` table while copying rows to extend its constraints. Deployment testing against populated secret stores remains advisable, but this is not a must-fix for the issue criteria.
- Several retryable internal queue or capacity conditions intentionally map to the single safe public reason `queue_full`; this is consistent with the specified public vocabulary.

## Verdict

**FAIL** - MF-1 remains an unresolved canonical Project-membership and idempotency correctness problem relative to the issue acceptance criteria.

<promise>FAIL</promise>
