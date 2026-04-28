## Why

The EXPLORE column on the Kanban board is always empty — no issue ever has `stage='explore'`. Explore sessions are managed on a separate `/explore` page and are not represented as issues in the workflow. The column exists only because the backend `Stage` enum includes `Explore`, creating a UI element that permanently displays "0 issues" and wastes horizontal space.

## What Changes

- Remove the `{ key: Stage.Explore, label: 'Explore' }` entry from the `STAGES` array in `KanbanBoard.tsx`

## Capabilities

### New Capabilities

### Modified Capabilities

- **web-ui**: Kanban board column set changes from 6 columns to 5, removing the permanently-empty Explore column

## Impact

- `packages/cli/web/src/components/KanbanBoard.tsx` — STAGES array (line 9)
- No backend changes required — the `Stage.Explore` enum value remains valid for the backend; only the Kanban display is affected
