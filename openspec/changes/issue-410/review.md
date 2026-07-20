# Review: Issue 410

## Findings

### P1: Production comments still expose ACP terminology

The ACP-removal requirement includes developer-facing source comments, but two
surviving runner comments still name the retired protocol:

- `packages/runner/src/runtime/executor.ts:151` says the AgentJob branch has
  "no `mohist/acp-agent`".
- `packages/runner/src/server/session-target.ts:96` gives `acp` as the example
  of a non-OpenCode legacy runtime.

The executable `REMOVED_ACTIONS` literal in `executor.ts:64` must remain to
produce the required actionable pre-cutover migration error. These comments do
not need that literal, however. Rewrite them in runtime-neutral terms (for
example, "removed Action" and "legacy runtime") so the production source no
longer exposes ACP terminology.

## Verification

- `npm test -w packages/runner` passed (1132 tests).
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore` passed (2849 tests).
- `npm run test:run -w packages/web` passed (4738 tests).

<promise>FAIL</promise>
