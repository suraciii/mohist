# Review: Issue 410

## Findings

### P1: OpenCode test fixtures still use ACP-prefixed runtime session ids

The planned ACP removal explicitly includes renaming legacy-string
`runtimeSessionId` fixtures. The current tree still contains many active
fixtures that pair `runtime: "opencode"` with values such as `"acp-1"`,
`"acp-old"`, and `"acp-123"`. Representative locations are
`packages/runner/tests/runner-signalr-followup.spec.ts:28`,
`packages/runner/tests/runner-signalr-cancel.spec.ts:190`,
`packages/runner/tests/server-connection-generic-session.spec.ts:27`,
`packages/web/src/widgets/coder-session/model/session-timeline-reducer.test.ts:275`,
`packages/web/src/widgets/coder-session/model/useSessionTimeline.dom.test.ts:30`,
and `packages/web/tests/SessionLiveUpdates.spec.tsx:30`.

These are ordinary OpenCode binding fixtures, not the required legacy-runtime
transition coverage, so their values should use neutral/current identifiers
such as `runtime-1`, `runtime-old`, and `runtime-123`. Sweep equivalent
fixtures across runner and web tests while retaining explicit legacy-binding
tests that exercise the Reset-hint behavior. This is required by the plan's
test-fixture sweep and avoids retaining the removed runtime identity in active
OpenCode examples.

## Verification

- `npm test -w packages/runner` passed.
- `npm run test:run -w packages/web` passed.
- `dotnet test Mohist.sln --no-restore` passed.

<promise>FAIL</promise>
