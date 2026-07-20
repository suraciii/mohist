# Review: Issue 410

## Findings

### P1: Obsolete acpSessionId terminology remains in test source

The ACP-removal plan requires the test/fixture sweep to remove every remaining
`acpSessionId` wire, server, and web term. The current branch still has negative
assertions that name the removed field in runner tests
(`packages/runner/tests/runner-signalr-followup.spec.ts:152` and
`packages/runner/tests/runner-signalr-followup-generic.spec.ts:160`), server
specs (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionTranscriptProjectionSpecs.cs:116`,
`GenericAgentSessionFollowupApiSpecs.cs:188`, and
`Specs/Issue/Api/IssueSessionApiSpecs.cs:107`), and the web test
`packages/web/src/app/providers/LiveTaskProvider.transcript.dom.test.ts:87`.

Replace these assertions with coverage of the current `runtimeSessionId` wire
contract (or remove them when redundant). Leaving the legacy identifier in test
source means the planned terminology sweep is incomplete and makes the removed
wire field continue to appear in the repository's active contract vocabulary.

## Verification

- `npm test -w packages/runner` passed.
- `npm run test:run -w packages/web` passed.
- `dotnet test Mohist.sln --no-restore` passed.

<promise>FAIL</promise>
