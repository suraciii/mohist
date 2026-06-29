## Why

The dashboard's completion signals — the factory-status "shipped today" count and the Digest "recently completed" list — are derived from an issue's `updatedAt`, not from when the issue actually reached a terminal state. `updatedAt` is bumped by any edit (a comment, a title tweak, a label change), so a long-done issue gets re-counted as "shipped today" and crowds genuinely recent completions off the list. The issue entity already persists `createdAt` and `archivedAt` but has no completion time, forcing every read-side consumer to approximate "when completed" from `updatedAt`. We need to give the issue a persisted completion time so "completed" has a single source of truth across the system.

## What Changes

- Add a persisted **completion time** field to the issue entity, symmetric with `createdAt` and `archivedAt`, recording the moment an issue enters a terminal state (`done` or `cancelled`).
- Write the field when the issue transitions into a terminal state, sourced from the `IssueWorkCompleted` event time. Reopening and re-completing updates it to the latest terminal moment (it is not cleared on reopen).
- Expose the field in the issue **list and detail read models** (and the archived read path) so every consumer reads the same source.
- **One-time backfill** of the completion time for issues already in a terminal state, deriving each value from the issue's completion event.
- Switch the dashboard **"shipped today" / completion snapshot** from `updatedAt` to the completion time — resolving the existing "forward-compatible with `completedAt`" placeholder in `dashboard-factory-status`.
- Switch the Digest **"recently completed"** list from ordering by `updatedAt` to ordering by the completion time, so editing a completed issue no longer re-surfaces it.
- **Non-goals**: no change to the completion-trend / throughput data sources (already event-based and correct); no new lead-time / cycle-time derived fields; the Digest "recently failed" category continues to order by `updatedAt` (failure has no in-scope persisted timestamp).

## Capabilities

### New Capabilities
- `issue-completion-timestamp`: The issue entity's persisted completion time — written when the issue enters a terminal state (`done`/`cancelled`) from the completion-event time, updated to the latest terminal moment on reopen-then-recomplete, and one-time backfilled for issues already terminal from their completion event. Establishes the single source of truth that read models and dashboard surfaces consume.

### Modified Capabilities
- `http-api`: Issue list, detail, and archived-detail read models SHALL expose the issue's completion time.
- `dashboard-factory-status`: The "shipped today" / completion snapshot SHALL be computed from the completion time instead of `updatedAt`, retiring the `updatedAt`-as-placeholder clause.
- `dashboard-recent-digest`: The "recently completed" category SHALL order by the completion time instead of `updatedAt`; the "recently failed" category ordering is unchanged.

## Impact

- **Server** (`packages/server`): new `CompletedAt` field on the issue entity and grain state (`Issue/Domain/Issue.cs`, `Issue/Grains/IssueGrain.cs`) plus a storage migration; terminal-state transitions in `Issue/Domain/Issue.Transitions.cs` write the field from the `IssueWorkCompleted` event time (`Issue/Domain/Events/IssueEvent.cs`), with reopen leaving it set and re-completion overwriting it; a one-time backfill over already-terminal issues; read-model mapping projects the field into the issue list/detail responses. TreatWarningsAsErrors enforces the build.
- **Web** (`packages/web`): issue entity type gains `completedAt`; `factory-status` derivation (`widgets/factory-status/model/`) switches `shippedToday` from `updatedAt` to `completedAt`; the Digest "recently completed" sort (`dashboard-recent-digest`) switches to `completedAt`; the archived page "Completed" label (`pages/archived/ui/ArchivedPage.tsx`) reads `completedAt`.
- **CLI**: read-only consumers of issue list/detail inherit the new field automatically; no required change.
- **No breaking external API change**: the field is additive on responses; the `updatedAt`→`completedAt` swap is an internal derivation change that makes the existing "shipped/recently completed" contracts behave as originally intended.
- **Tests**: entity/grain terminal-transition write, reopen-then-recomplete overwrite, backfill correctness from events, read-model projection, and dashboard derivations no longer reacting to post-completion edits.
