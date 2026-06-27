## Why

After creating an issue, the success toast shows `undefined` instead of the new issue's number, so users cannot confirm which issue was just created or navigate to it. The backend returns the correct `number`; the Web create flow renders the wrong field. With the expected "Issue #223 created" confirmation, the create flow gives no reliable feedback at all.

## What Changes

- The create-issue success toast SHALL display the newly created issue's `number`, e.g. `Issue #223 created`.
- The create-issue mutation SHALL read `number` from the create API response (`Issue.number`) when building the toast message.
- A failing create SHALL still surface an error toast without referencing an undefined number.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `web-ui`: Add a requirement that the issue create flow confirms the new issue with a success toast showing the correct issue number (currently renders `undefined`).

## Impact

- `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx` — `useMutation` `onSuccess` consumes the create response and emits the success toast.
- Corresponding create-issue tests (`CreateIssueDialog` test / `queries` test) assert the rendered number.
- No API contract change: `POST /issues` already returns the full `Issue` with `number`.
