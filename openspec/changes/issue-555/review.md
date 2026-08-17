# Review

This is a re-review of the current change. The issue details were read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; the rendered issue body is empty, so the acceptance contract was re-read from `proposal.md`, `design.md`, and all five capability specs under this change. Verdict: **FAIL**.

## Must-fix Findings

### MF-1: Projection-lag reads ignore the durable lifecycle-history feed

**Where:** `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicExecutionReadQuerier.cs:291-339`; the projector discovers and checkpoints `PublicProjectionFeeds.AgentSessionLifecycle` in `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicApiProjectionEngine.cs:236-245` and `:359-362`.

The read freshness gate includes the Session state digest, the AgentSession event journal, and joined Job feeds, but it never compares the Session lifecycle-transition table's head with its lifecycle checkpoint. This is observable when a Session has already been projected at state digest U, then its canonical state changes U -> Active -> U before the next sweep: the lifecycle rows contain both new transitions, while the current state digest is again U. `IsSessionProjectionBehindAsync` therefore returns false and the events route can serve a page missing those transitions instead of returning `503 projection_lag`.

This violates the public read/event-stream freshness acceptance that a read whose required durable source watermark is ahead of its checkpoint returns `503 projection_lag`, and it breaks the projection requirement that the event journal and checkpoint agree on all consumed canonical lifecycle facts. The freshness comparison must include the lifecycle feed (or an equivalent monotonic watermark) so compressed transitions cannot be served as caught up.

### MF-2: Retryable queued dispatch states omit the required safe public error

**Where:** `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicExecutionAggregator.cs:748-754` and `:789-800`, with DTO construction in `PublicApiProjectionEngine.cs:845-870`.

`IsDispatchBlocked` correctly recognizes `capacity-full`, `concurrency-limit`, and `no-online-runner` and `ResolveAdmission` sets `admission=blocked`, but `ResolveReasonCode` only maps unknown facts. `PublicAnchorComponents.Error` also remains null for this path. Consequently a queued, capacity-blocked execution is serialized with `status=queued`, `admission=blocked`, `turnStatus=queued`, `reasonCode=null`, and `error=null`.

This violates the `public-execution-read` acceptance scenario requiring a retryable dispatch block to retain `status=queued` and `turnStatus=queued` while exposing a safe public error, and the contract's `queue_full` safe reason vocabulary. The projection must map the canonical retryable block to a stable public reason/error, without exposing the internal wait reason.

### MF-3: A matching retry can return 200 while an unresolved stop mapping is still pending

**Where:** `packages/server/src/Mohist.Server/Api/DirectApi/DirectApiStopRoutes.cs:126-130`.

When a retry finds its existing pending mapping and the canonical stop claim still matches the frozen target, the route immediately calls `ReadTurnObservationAsync`. If the public projection has caught up, that path returns `200` with the current Turn observation even though the durable mapping remains `pending` and the stop outcome is not confirmed. The newly added `StopPending()` response is used only after a fresh `StopAsync` result is unresolved, not on this matching-retry path.

This violates the unresolved-stop requirement that a matching retry resolve the same mapping/operation/outcome and the shipped `stop_pending` contract: a keyed stop whose fenced outcome is not confirmed must remain retryable with `503 stop_pending`, not look like a completed command merely because its public snapshot is current. The matching retry must return the pending error until the reconciliation path completes the mapping, while preserving the frozen operation and avoiding redelivery.

### MF-4: Launch replay is blocked by a later Agent archive before the idempotency mapping is read

**Where:** `packages/server/src/Mohist.Server/Api/DirectApi/DirectApiRoutes.cs:459-477`.

The launch route resolves and rejects an archived Agent before looking up the durable mapping. A launch can therefore complete and persist its Job mapping, the Agent can subsequently be archived, and an identical retry with the same Project, Agent, key, and body returns `404 agent_not_found` instead of the original canonical mapping and current public observation. The same ordering pattern exists for follow-up Session resolution at `:238-259`.

This violates the idempotency acceptance that an identical replay of the same scope, key, and accepted body returns `200` with the original canonical mapping and current observation. Resource admission for a new key must still reject missing/archived resources, but an existing matching mapping must be recoverable after the resource's mutable status changes; the route needs a replay path that does not discard the durable mapping because the current definition is no longer writable.

## Previous Findings

- **Previous MF-1, dropped lifecycle transitions:** fixed properly. `AgentSessionStore` now records public-relevant lifecycle history transactionally with the mutable Session row, and the projector consumes historical snapshots rather than inferring only from the latest aggregate state. The compressed queued/running/terminal and distinct unknown-episode tests pass. The architecture regression was also addressed by keeping persisted history values below the application-layer type boundary.
- **Previous MF-2, missing deleted-Session tombstones:** fixed properly. `AgentSessionStore.DeleteAsync` closes the existing public stream in the deletion transaction, and `IAgentSessionStreamRetention.PurgeDeletedAsync` explicitly removes the tombstone and retained public rows only after closure. The deletion, valid-cursor `410`, no-cursor `404`, and post-purge `400` scenarios pass.

## Dimension Checks

- **Issue contract and acceptance criteria:** checked before reviewing the implementation; the criteria were reconstructed from the proposal, design, task acceptance criteria, and five capability specs because `mo issue view` returned an empty body.
- **Coverage:** FAIL. The direct auth boundary, public DTO allowlist, projection transactions/fences/generations, resource reads, keyed writes, stop fencing, cursor validation, deletion retention, and shipped documentation are present. The four must-fix gaps above leave lifecycle freshness, blocked-state explanation, unresolved-stop replay semantics, and full idempotent launch replay incomplete.
- **Correctness:** FAIL for the four findings above. The prior lifecycle-compression and tombstone failures no longer reproduce in their focused scenarios.
- **Consistency:** checked, no additional issue found. The implementation follows the existing middleware, EF, grain, event-store, public JSON, and direct-route conventions; the public API remains separated from control-plane read shapes.
- **Tests and verification:** checked. `npm run verify` passed: docs, file-size, format, build, 3,980 Server SpecTests, 2,676 Server unit tests, 69 architecture tests, 178 workflow tests, 1,848 CLI tests, 4,724 Web tests, 1,639 Runner tests, and 70 Slack tests. The passing suite does not cover a lifecycle state cycle compressed between sweeps, the required queued-block error/reason fields, a matching retry while a stop claim remains unresolved, or replay after an Agent is archived.

## Observations

- `20260909000000_AddPublicApiCursorSecret.cs` rebuilds the existing `StoredSecrets` table to extend its check constraints for the persisted cursor key. It copies existing rows, but this is broader migration work than the additive public projection tables and deserves deployment testing against populated secret stores. This remains an observation, not an additional must-fix finding.
- The retention path necessarily updates/deletes public stream rows outside the hosted projector to close and purge a deleted Session. That is a deliberate exception required to preserve the tombstone after the canonical Session row is removed; the tested behavior is correct.

<promise>FAIL</promise>
