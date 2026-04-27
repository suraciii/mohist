## Context

`KanbanBoard.tsx` defines a `STAGES` array that drives column rendering on both mobile (tab strip) and desktop (multi-column layout). Currently it includes all 6 values from the backend `Stage` enum, including `Stage.Explore`. No issue ever enters the explore stage — explore sessions live on a separate `/explore` page — so the column is permanently empty.

Historical note: a similar mismatch bug (Issue #14) occurred when Kanban used `Check` instead of `Review`. The fix at that time aligned Kanban with the full enum, which introduced this empty Explore column.

## Goals / Non-Goals

**Goals:**
- Remove the always-empty Explore column from the Kanban board
- Maintain existing behavior for all other stages

**Non-Goals:**
- Changing the backend `Stage` enum or workflow engine
- Modifying the `/explore` page or explore session management

## Decisions

### D1: Remove Explore from frontend STAGES array only

Delete the `{ key: Stage.Explore, label: 'Explore' }` entry from `STAGES` in `KanbanBoard.tsx`. The `Stage` enum in `types.ts` remains unchanged — it is still the source of truth for backend stage values.

**Alternatives considered:**
- Filter at render time (e.g., `STAGES.filter(s => s.key !== Stage.Explore)`) — adds unnecessary runtime logic for a static configuration
- Remove `Stage.Explore` from the enum entirely — would require backend changes and break the explore feature

## Risks / Trade-offs

- [Any future issue that enters `stage='explore'` won't appear in Kanban] → This is expected behavior per the spec; explore sessions are managed on `/explore`, not as Kanban issues

## Migration Plan

No migration needed. Single-line deletion, deploy with next build.

## Open Questions

None.
