## Why

Issue creation is currently inconsistent across Mohist surfaces: the backend already supports `priority` and `model`, but Web UI issue forms still hide priority and the CLI still cannot set a model at creation time. As issue volume grows, the Kanban board also lacks the filtering, sorting, and search controls needed to keep urgent work visible, so this parity and discoverability gap is now daily user friction rather than a nice-to-have.

## What Changes

- Add issue creation parity so Web UI create/edit flows can read and write `priority`, and CLI `mo issue create` can set `--model` in the initial create request instead of requiring a follow-up update.
- Extend issue creation and update request typing across the Web UI client so `priority` is treated as a first-class field alongside existing labels and model metadata.
- Enhance the Kanban board with priority filtering, label filtering, title search, and per-column sort switching (`priority`, `number`, `updated`) so large backlogs can be narrowed without leaving the board.
- Persist board filter, sort, and search state in the URL so focused views survive refresh, sharing, and mobile single-column navigation.
- Keep CLI and API model validation behavior aligned so invalid `provider/model` input fails clearly during `mo issue create`, including when combined with `--body @file` or `--body -`.

## Capabilities

### New Capabilities


### Modified Capabilities

- `cli-interface`
- `http-api`
- `web-ui`

## Impact

- `packages/cli/src/cli/commands/issue.ts` will add `--model` handling to `mo issue create` and reuse existing body-ingestion flow and server-side validation.
- `packages/cli/src/api/issues.ts` remains the validation boundary that the CLI create flow relies on for malformed `provider/model` input.
- `packages/cli/web/src/lib/api.ts`, `packages/cli/web/src/lib/types.ts`, and `packages/cli/web/src/hooks/useQueries.ts` will need updated request/query typing for priority, filters, and sort state.
- `packages/cli/web/src/components/CreateIssueDialog.tsx` and `packages/cli/web/src/components/EditIssueDialog.tsx` will gain priority controls, while `packages/cli/web/src/components/KanbanBoard.tsx`, `StageColumn.tsx`, and related board helpers will absorb filter/sort/search UI and mobile-compatible URL-backed state.
- Existing priority color semantics should stay aligned with current CLI formatting and IssueCard presentation rather than introducing a new priority vocabulary or storage model.
