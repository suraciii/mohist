## Why

Issue Detail page descriptions are truncated at 600px with a gradient overlay, but the Expand/Collapse button is positioned inside the overflow-hidden container where it can be obscured by the gradient or clipped by the scroll boundary. Users cannot reliably discover or interact with the control, causing them to believe long descriptions are permanently truncated. This directly impacts readability of design specs, acceptance criteria, and issue context.

## What Changes

- Reposition the Expand/Collapse control outside the overflow-clipped description container so it is always visible and clickable.
- Conditionally render the Expand button only when description content actually exceeds the 600px threshold; hide it for short descriptions.
- Ensure the Collapse button restores the constrained view after expansion.
- Add or update frontend tests for the expand/collapse interaction, including threshold-based conditional visibility.

## Capabilities

### New Capabilities

<!-- List new capabilities (kebab-case). Each becomes specs/<name>/spec.md. Leave empty if none. -->

### Modified Capabilities

- `web-ui` — `REQ-WUI-ISSUE-MARKDOWN-003` (Issue Detail collapses long descriptions): requirements need refinement to specify button positioning, threshold-based conditional visibility, and interaction test coverage.

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — description expand/collapse layout and logic.
- `packages/cli/web/tests/IssueDetailPage.test.tsx` — add/update tests for conditional expand button visibility and interaction.

