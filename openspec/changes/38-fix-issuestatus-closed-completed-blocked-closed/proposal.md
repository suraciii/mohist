## Why

Frontend `IssueStatus` enum is missing `Closed` and `Completed` values that the backend already produces, causing these statuses to render as generic gray badges. Additionally, `IssueCard` displays `Blocked` status with the label "Closed" (red text), which is semantically wrong — `Blocked` means the pipeline failed, while `Closed` means the user manually closed the issue.

## What Changes

- Add `Closed = 'closed'` and `Completed = 'completed'` to the frontend `IssueStatus` enum in `web/src/lib/types.ts`
- Fix `IssueCard.tsx` line 76: change "Closed" label to "Blocked" for `Blocked` status
- Add distinct badge styling for `Closed` and `Completed` statuses in `statusBadge()` (IssueDetailPage.tsx)
- Add status indicator for `Closed` and `Completed` in `IssueCard.tsx`

## Capabilities

### New Capabilities

### Modified Capabilities

- `web-ui` — status badge rendering requirements expand to cover all 6 backend statuses with correct labels and colors

## Impact

- `packages/cli/web/src/lib/types.ts` — enum definition
- `packages/cli/web/src/components/IssueCard.tsx` — card status display
- `packages/cli/web/src/components/IssueDetailPage.tsx` — detail page badge
- No backend changes; no API changes; no breaking changes
