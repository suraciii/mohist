# Review

This is the first review of the current change. The issue details were read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; the issue body is empty, so the acceptance contract was re-read from `proposal.md`, `design.md`, and all five capability specs under this change.

## Must-fix Findings

### MF-1: The public event stream drops durable lifecycle transitions

**Where:** `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicApiProjectionEngine.cs:325` and `:423`; `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicExecutionAggregator.cs:495` and `:523`.

`ProjectSessionAsync` builds `PublicProjectionFacts` from one current `AgentSession` ledger row. `DeriveTransitions` then emits a turn event only for the turn's current status. The loaded `AgentSessionEventRow` journal is used for runtime-binding/context-reset facts, not for the input/turn lifecycle transitions, and the AgentJob journal contents are used only to make a target dirty. Consequently, if a turn is durably queued, then running, then terminal before a projector batch observes it, the public journal can contain `input.accepted` and `turn.terminal` but no `turn.queued` or `turn.running`. The checkpoint still advances past the consumed source state, so a later sweep cannot recover the missing events. The fixed `session:unknown` identity also suppresses later distinct unknown episodes.

This makes the durable per-Session event stream incomplete and violates the `public-execution-projection` requirement that the projection commit the corresponding public Session event journal for consumed canonical facts, as well as the issue proposal's acceptance of durable cursor-based Session event reads. A client resuming the stream cannot observe all lifecycle transitions that occurred. Lifecycle transitions need durable source identities/history that the projector consumes, rather than being inferred solely from the latest mutable aggregate state.

### MF-2: Production Session deletion never creates a closed-stream tombstone

**Where:** `packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionStore.cs:161`; `packages/server/src/Mohist.Server/Infrastructure/Data/PublicApi/PublicProjectionRows.cs:173`; `packages/server/src/Mohist.Server/Infrastructure/PublicApi/PublicSessionEventStreamQuerier.cs:61`.

`AgentSessionStore.DeleteAsync` deletes only the canonical `AgentSessions` row. `PublicStreamStateRow.Closed` is declared and the event route has a branch for it, but no production deletion path sets `Closed = true` or otherwise creates/retains a tombstone. The current stream specs set `Closed` directly through test-only database setup, so they do not verify the actual deletion behavior. After a real Session deletion, a valid current-generation cursor reaches the canonical Project lookup and returns `session_not_found` at `PublicSessionEventStreamQuerier.cs:70-75`, instead of the required `410 cursor_expired` with `earliestSequence=null` and the last safe `latestSequence`.

This violates the `public-session-event-stream` retention requirement and T-007's acceptance criterion: a deleted Session must retain a minimal tombstone during the cursor-retention window, return 410 for a valid cursor, return 404 without a valid cursor, and become `cursor_invalid` only after physical purge. The deletion path must close the public stream and preserve its safe bounds transactionally with the deletion/retention operation, and the purge path must be explicit.

## Dimension Checks

- **Acceptance coverage:** must-fix gaps found above in the durable Session event stream and deleted-Session tombstone behavior; the other route, auth, idempotency, projection-read, cursor-validation, and documentation criteria have corresponding implementation and test coverage.
- **Correctness:** must-fix gaps found above. The auth ordering, public allowlist, projection lag response, keyed replay/conflict paths, stop fencing, cursor binding, and generation sequencing were inspected against their failure cases.
- **Consistency:** checked, no issue found in the changed code's use of the existing middleware, grain, EF, event-store, and public JSON patterns. The public API remains separated from control-plane read shapes.
- **Tests and verification:** the repository gate is green (`npm run verify`): docs and format checks, solution build, 3,977 Server SpecTests, 2,676 Server unit tests, 69 architecture tests, 1,848 CLI tests, 4,724 Web tests, 1,639 Runner tests, and 70 Slack tests. However, the suite does not cover the two failure cases above: it tests public event rows from manually seeded/current facts and manually toggles `Closed` in the database rather than exercising lifecycle compression and the production deletion path.

## Observations

- `20260909000000_AddPublicApiCursorSecret.cs` rebuilds the existing `StoredSecrets` table to extend its check constraints for the persisted cursor key. It copies existing rows, but this is broader migration work than the additive public projection tables and deserves deployment testing against populated secret stores. It is recorded as an observation, not an additional must-fix finding.
- The worktree was clean after verification; no product file was modified during this review.

<promise>FAIL</promise>
