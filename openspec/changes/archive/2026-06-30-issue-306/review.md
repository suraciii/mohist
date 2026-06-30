# Review Report

## Result: PASS

## Repaired Items

_(none)_

## Blocking Items

_(none)_

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/IssueTemplate/IssueTemplateApiSpecs.cs`
  Evidence: The API list test verifies `feature` shape and source at `IssueTemplateApiSpecs.cs:29`, while registry tests verify all three built-ins (`feature`, `bug`, `refactor`) at `IssueTemplateRegistrySpecs.cs:63` and the integration suite exercises production file loading. This is enough for the current change, but an API-level assertion for `bug` and `refactor` would make the HTTP acceptance criterion directly traceable.
  SuggestedAction: Add `Assert.Single`/`Assert.Contains` checks for `bug` and `refactor` in `List_IncludesBuiltinTemplates` if this surface changes again.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/entities/issue-templates/api/client.ts`, `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`
  Evidence: The Web and CLI clients intentionally leave `/` unescaped so the legacy `mohist/default` alias and slash-bearing custom ids route through the server catch-all path (`client.ts:8`, `MohistCliCommands.Issue.cs:2111`). That preserves the compatibility requirement, but ids containing reserved URL characters such as `?` or `#` would still corrupt the request path. This is not introduced by the reviewed change and custom-template write-path constraints are out of scope.
  SuggestedAction: When custom template creation is designed, define the allowed template-id character set or add a path-segment encoding strategy that preserves slash aliases while escaping other reserved characters.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: dependency hygiene
  Evidence: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"` builds the Web assets and npm reported `9 vulnerabilities (3 moderate, 3 high, 3 critical)`. The command still passed and this is unrelated to the issue-template implementation.
  SuggestedAction: Triage npm audit findings separately.
  Status: out-of-scope

## Verification Summary

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"` - passed, 46 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter "FullyQualifiedName~CliIssueTemplateCommandSpecs"` - passed, 19 tests.
- `npm run typecheck -w packages/web` - passed.
- `npm run test:run -w packages/web -- --run src/entities/issue-templates/api/client.test.ts src/entities/issue-templates/api/queries.test.ts src/features/create-issue/ui/CreateIssueDialog.test.tsx` - passed, 35 tests.
- `npm run test:run -w packages/web` - passed, 198 files, 2988 tests passed, 1 skipped.
- `npm test` - passed: .NET 3136 passed / 13 skipped, Web 2988 passed / 1 skipped, Runner 786 passed / 23 skipped.

<promise>PASS</promise>
