## Why

Approvers often reach an issue on a phone but cannot read the decision evidence without opening separate artifact dialogs, so they either approve from file names or postpone the decision. Bringing the relevant evidence and guided feedback into the approval view makes plan and check decisions faster and more specific while preserving the workflow's existing approval contract.

## What Changes

- Replace the generic decision presentation while an issue awaits approval with an inline review package that keeps the evidence and approve/send-back actions together; non-approval states retain the existing decision-surface behavior.
- For plan approval, show `proposal.md` and `tasks.json` directly on the issue page without requiring an artifact dialog.
- For check approval, show `review.md` and the current diff summary directly on the issue page without requiring a dialog.
- Make approval evidence readable on phone-width viewports with no horizontal page scrolling, and place direct thumb-reachable Approve and Send back controls alongside it: Approve completes in one tap, Send back opens its structured inline form in one tap, and neither action requires the generic action drawer or another dialog.
- Add direction, scope, and detail choices to the send-back form alongside free text, while continuing to submit one feedback text payload through the existing workflow contract.
- Add discoverable desktop shortcuts: `a` approves, `m` opens send-back, and Command+Enter submits both send-back feedback and issue comments.

## Capabilities

- `issue-decision-surface`: Extends the unified issue decision surface with stage-specific inline approval evidence, responsive approval actions, and structured send-back guidance while preserving non-approval behavior and the existing text feedback contract.
- `issue-detail-keyboard-actions`: Defines discoverable desktop keyboard actions for approving, opening send-back, and submitting send-back or comment textareas from the issue detail page.

## Impact

- Affects the Web issue detail composition, unified decision surface, narrow-viewport action presentation, artifact content rendering, diff-summary placement, send-back form, comment composer, keyboard shortcut registration, and focused component/browser specifications under `packages/web/`.
- Reuses the existing artifact list/content, diff summary, approval, feedback, and comment APIs; no server API, persistence model, workflow, runner, CLI, or dependency changes are expected.
- The established `issue-decision-surface` capability gains approval-specific behavior; all other issue states must remain behaviorally unchanged.
