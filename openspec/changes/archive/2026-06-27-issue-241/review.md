# Review Report

## Result: PASS

Reviewed the post-build candidate snapshot for issue 241. The product deliverable is the CLI change outside `openspec/changes/issue-241/`; workflow artifacts were treated as review context only.

## Repaired Items

_None._

## Blocking Items

_None._

Acceptance and implementation evidence:

- `mo issue session --help` is backed by the registered singular command group and four subcommands in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:854`, `:860`, `:861`, `:862`, and `:863`; the help text documents that `<name>` comes from `mo issue sessions <num>` at `:856` and `:868`.
- Existing plural `mo issue sessions <num>` remains on the pre-existing `/coder-sessions` path in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:814` and `:845`, and the regression test asserts that endpoint in `packages/cli/tests/Mohist.Cli.Tests/CliIssueSessionSpecs.cs:49`.
- `session show` sends `GET /issues/{number}/sessions/{name}` and supports `--project/--project-id` plus `-o table|json` through the shared output validation and renderer path in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:873` through `:913`; table metadata rendering covers status, model, created time, part/tool counts, tokens, context usage, and health in `packages/cli/Mohist.Cli/TableRenderer.Issues.cs:302` through `:338`.
- `session transcript` sends `GET /sessions/{name}/transcript`, defaults to table output for the long transcript case, and summarizes turns/parts/first/last activity in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:918` through `:958` and `packages/cli/Mohist.Cli/TableRenderer.Issues.cs:355` through `:373`. JSON mode still uses the shared envelope path to emit raw `data` from `packages/cli/Mohist.Cli/MohistCliApi.cs:522` through `:528`.
- `session compact` and `session reset` POST to the existing recovery endpoints with an empty JSON body and use `SessionRecovery` table rendering in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:963` through `:1004` and `:1009` through `:1050`; the renderer prints `New session: <agentSessionId>` from the recovery payload in `packages/cli/Mohist.Cli/TableRenderer.Issues.cs:375` through `:395`.
- 409 `session_active` passthrough is preserved by routing non-success envelopes through `PrintResponseAsync`, which prints `error (code)` and exits non-zero in `packages/cli/Mohist.Cli/MohistCliApi.cs:531`, `:749`, and `:767` through `:770`. The server emits the relevant conflicts in `packages/server/src/Mohist.Server/Api/IssueRoutes.Sessions.cs:48` through `:70` and `:73` through `:95`.
- Response field names match the existing server DTO contracts: metadata fields in `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs:53` through `:65`, transcript fields in `:71` through `:76`, and recovery `AgentSessionId` in `packages/server/src/Mohist.Server/Sessions/Grains/IAgentSessionGrain.cs:84` through `:93`.
- New tests cover help, the preserved plural list command, table/json success paths, 404 handling, 409 active-session conflict passthrough, project override flags, and invalid output modes in `packages/cli/tests/Mohist.Cli.Tests/CliIssueSessionSpecs.cs:30` through `:570`.

Verification:

- `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` passed with 0 warnings and 0 errors.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed: 380 passed, 0 failed, 0 skipped.
- `dotnet build Mohist.sln -p:SkipWebBuild=true` passed with 0 warnings and 0 errors.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliIssueSessionSpecs.cs`
  Evidence: The new command implementation escapes issue/session/project path segments with `MohistCliCommands.Escape` in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:906`, `:951`, `:996`, and `:1042`, but the new tests only assert plain session names such as `plan`. This does not break the reviewed change because the implementation uses the established escaping helper consistently.
  SuggestedAction: Add a future regression test with a session name requiring URL escaping if server-side session names become less constrained than stage-like names.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None._

<promise>PASS</promise>
