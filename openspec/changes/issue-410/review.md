# Review: Issue 410

## Findings

### P1: Four ordinary OpenCode fixtures still use ACP-prefixed session ids

The fixture sweep in `6fa58340f` missed four values that still attach an
ACP-prefixed id to ordinary OpenCode sessions:

- `packages/web/tests/browser/coder-session-compact-viewport.spec.ts:59,107`
  sets `runtime: "opencode"` with ``acp-${sessionName}``.
- `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/GenericAgentSessionSummarySpecs.cs:254,265,334,409`
  seeds summary and transcript records with `acp-` identifiers without testing
  a legacy runtime.

Rename these to neutral runtime/session identifiers while preserving the
explicit `runtime: "acp"` tests that cover the legacy Reset-hint transition.
The issue plan specifically requires legacy-string runtime-session fixtures to
be renamed; these leftovers keep the removed identity in current OpenCode test
data.

## Verification

- `npm test -w packages/runner` passed.
- `npm run test:run -w packages/web` passed.
- `dotnet test Mohist.sln --no-restore` passed.

<promise>FAIL</promise>
