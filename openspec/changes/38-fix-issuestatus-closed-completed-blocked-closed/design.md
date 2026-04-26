## Context

The backend `IssueStatus` enum (`packages/cli/src/types/index.ts:32`) defines 6 statuses: `Active`, `Paused`, `Blocked`, `Interrupted`, `Closed`, `Completed`. The frontend enum (`packages/cli/web/src/lib/types.ts:10`) only defines 4 — missing `Closed` and `Completed`. When the API returns `"closed"` or `"completed"`, the frontend `statusBadge()` switch falls through to a generic gray default.

Additionally, `IssueCard.tsx:76` renders `Blocked` with the label "Closed" (red text), conflating two distinct states: pipeline failure vs. user-initiated closure.

## Goals / Non-Goals

**Goals:**
- Sync frontend `IssueStatus` enum with backend (add `Closed`, `Completed`)
- Fix `Blocked` label in IssueCard ("Blocked" instead of "Closed")
- Add distinct visual styles for `Closed` and `Completed` in both IssueCard and IssueDetailPage
- Add Reopen button for `Closed` and `Completed` statuses in IssueDetailPage Actions

**Non-Goals:**
- Redesigning the status badge system or color palette
- Changing backend status values or adding new statuses
- Changing IssueCard layout or adding new card indicators beyond the status label fix
- Adding new API endpoints

## Decisions

### D1: Direct enum extension in types.ts

Add `Closed = 'closed'` and `Completed = 'completed'` to the frontend `IssueStatus` enum. This is a straightforward sync — the string values already match what the backend sends.

**Alternatives considered:** None — this is the only correct approach.

### D2: Color scheme for Closed and Completed

| Status | IssueCard | IssueDetailPage badge |
|--------|-----------|----------------------|
| Closed | `text-gray-500` | `text-gray-700 bg-gray-50` |
| Completed | `text-green-500` | `text-green-700 bg-green-50` |

Closed uses gray (neutral, user-actioned terminal state). Completed uses green (positive, successful pipeline completion). Blocked stays red (error/negative).

**Alternatives considered:** Using the same gray for both Closed and Completed — rejected because Completed implies success and should be visually positive.

### D3: Reuse existing reopenMutation for Closed/Completed actions

IssueDetailPage already has `reopenMutation` for Blocked/Interrupted. Extend the Actions section to show "Reopen" for `Closed` and `Completed` statuses using the same mutation. The backend `reopen()` method already accepts `closed` and `completed` as valid reopen-from states (`issue-service.ts:112`).

### D4: Blocked label fix in IssueCard

Change line 76 from `"Closed"` to `"Blocked"`, keeping the existing red text color.

## Risks / Trade-offs

- [Future IssueCard redesign may change these indicators] → The label/color fix is minimal and easy to adjust when Issue #30's card redesign lands.
- [statusBadge default case becomes unreachable for known statuses] → Keep the default case as a safety net for any unknown future statuses.

## Migration Plan

Frontend-only change. No migration needed. Deploy with standard build.

## Open Questions

None.
