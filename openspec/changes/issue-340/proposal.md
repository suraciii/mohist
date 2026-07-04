# Proposal

## Summary

Converge the issue detail page around a single runtime decision surface. The page should derive one current runtime summary and one primary user action from the workflow projections, while secondary cards remain informational or handle non-overlapping issue metadata actions.

## Scope

- Use one runtime decision model for running, queued, approval, blocked, failed, and done states.
- Route start, stop, retry, resume, rerun, approve, and send-back through shared issue-detail mutations.
- Present a single user-facing Stop action and choose the recoverable or terminal backend stop mutation from runtime recoverability.
- Keep workflow evidence visible without exposing duplicate write controls from the embedded workflow view.
- Normalize issue-detail surfaces to theme-token card and status colors.

## Non-Goals

- Do not change backend workflow, recovery, approval, convergence, or drift projections.
- Do not redesign the overall issue-detail information architecture.
- Do not add mobile-only action drawers or keyboard shortcuts.
