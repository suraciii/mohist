# Self-Review (round 2) — issue-527 (Slack files as Agent input)

## Verdict summary

Round 1 failed the plan on a single blocker (F1: unsound redelivery idempotency for attachments). The fix adopted **message-scoped deterministic attachment ids + insert-if-absent/fetch-if-absent ingest + a stated ack-timing assumption**. This round verifies the fix is sound — including the ack-timing assumption, which is now **confirmed against the adapter code**, not merely asserted — and finds no new blocking issues. Minor implementation reminders are noted below but are build-time details, not plan gaps.

**Promise: PASS**

---

## Round-1 findings — status

### F1 (was BLOCKER) — RESOLVED and verified

The corrected D5 model is sound:

- **Idempotent ingest.** D3's `IngestProviderFileAsync` now takes a caller-supplied deterministic id and is insert-if-absent; the route fetches only when the row is absent. This makes redelivery a storage-level no-op (no re-fetch, no duplicate row/bytes) — directly satisfying spec scenario *"A redelivered message does not duplicate attachments"* (`spec.md:121`–`125`).
- **Message-scoped id is the right key.** `att_{StableToken(teamId/conversationId/messageTs/slackFileId)}` is stable across redelivery of the same message, yet differs for the same physical file in a different message — so it gains redelivery idempotency without introducing the cross-session `AlreadyBound` that the rejected bare `att_slack_{fileId}` would cause. The previously-rejected alternative is now adopted in its correct, scoped form.
- **Same-owner re-validation is real.** Verified `AttachmentService.cs:418`–`423` + `:443`–`450`: a row already owned by the target owner takes neither the `AlreadyBound` branch nor the newly-claimed path; it is reported accepted with an empty `newlyBoundIds`. So `ValidateAndBindAgentInputAsync` needs no change.
- **Ack-timing assumption — VERIFIED, not assumed.** D5's load-bearing claim ("the adapter does not ack Slack until the Server responds") is confirmed at `packages/mohist-slack/src/adapter.ts:106`–`108`: the handler `await`s `transport.ingress(...)` (the Server HTTP POST) and only then calls `ack()`. Therefore a Server crash before its HTTP response → no adapter ack → Slack redelivers → the route re-runs against the same deterministic owner and ids → idempotent completion. This substantiates spec scenario *"A restart does not lose pending file binding"* (`spec.md:127`–`130`) via Slack redelivery, with no need for a Server-side inbox sweep.

The launch-block re-execution window that round 1 identified (`SlackConnectionRoutes.cs:1133` re-runs when `route.SessionId` is null, regardless of `AlreadyExisted`) is now harmless because every per-file side effect is idempotent under the deterministic id.

### F2 (consistency) — RESOLVED
`proposal.md:23` now states the provider inbox is unchanged (dedups by message identity) and the deterministic id is re-derivable on every delivery; the contradictory "inbox carries file references" claim is gone. Proposal ↔ design ↔ tasks are consistent on this point.

### F3 (minor) — RESOLVED
T-003 now instructs including the deterministic attachment ids in the launch idempotency fingerprint (mirroring `AgentSessionLaunchRoutes.cs:102`–`137`). Because the ids are stable across redelivery, this will not falsely conflict on replay.

### F4 (minor) — RESOLVED
D2 notes the Slack file id is transient (consumed to compute the opaque id, then discarded); D5 layer 1 specifies the id is an opaque `StableToken` output that does not embed the raw Slack file id, preserving the `spec.md:151`–`154` invariant.

---

## New check — no blocking issues found

- **Concurrent same-message delivery.** Two concurrent POSTs of the same Slack event could both pass into the launch block. The deterministic-id insert-if-absent must therefore be **atomic** — backed by the existing `AttachmentRow.Id` primary key (insert-or-catch-unique-violation / upsert), not a check-then-insert TOCTOU. This is a build-time implementation requirement; the design's "insert-if-absent" wording implies it, but T-002 should make the atomicity explicit. Flagged as a minor reminder below, not a blocker (the PK constraint is the safety net either way).
- **Spec ↔ design alignment.** The two redelivery/restart spec scenarios are now substantiated by the verified mechanism; no spec change is required.
- **Format/mechanical.** `tasks.json` remains valid JSON, acyclic DAG, all `dependsOn` → strictly lower priority. Spec headings unchanged (9 requirements, all scenarios at `####`).

---

## Minor implementation reminders (non-blocking, for the builder)

1. **T-002:** implement `IngestProviderFileAsync`'s insert-if-absent atomically against the `AttachmentRow.Id` uniqueness constraint (upsert or catch-duplicate-key), so concurrent same-message deliveries collapse to one row.
2. **T-003:** confirm the connection-launch envelope populates `AgentLaunchCoordinatorRequest.AttachmentIds` with the deterministic ids so the coordinator's conflict fingerprint is attachment-aware and stable across redelivery.
3. **Open Question (unchanged):** `files:read` scope handling — advertise at setup, degrade at runtime. Not a plan blocker.

## Recommendation

The plan is ready to build. Round 1's blocker is resolved with a verified mechanism; the artifacts are internally consistent; the task graph is sound.

<promise>PASS</promise>
