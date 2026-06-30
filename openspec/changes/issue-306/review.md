# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Api/IssueTemplateRoutes.cs`
  Evidence: The detail endpoint computes `source` from the requested name instead of the template that was actually returned: `var source = registry.IsBuiltin(name) ? "builtin" : "custom"` at `IssueTemplateRoutes.cs:29-37`. When built-ins are disabled and a project custom template shadows `feature`, `registry.Get("feature", projectId)` correctly returns the custom template (`IssueTemplateRegistry.cs:72-89`), but `registry.IsBuiltin("feature")` still returns true (`IssueTemplateRegistry.cs:147-148`). The API test `DisabledBuiltIn_CanBeShadowedByProjectCustomTemplate` verifies the custom name/description but does not assert detail `source`, so this regression is currently hidden. This breaks the list/detail source contract and will cause CLI/Web detail consumers to display a shadowed custom template as builtin. [disallowed: product behavior/API contract change]
  SuggestedAction: Determine source from the resolved template context, not the raw request id. For example, have `IssueTemplateRegistry.Get` return source with the template, or add a source-aware lookup that respects disabled-built-in shadowing and aliases. Add an API test asserting detail `source == "custom"` for the disabled/shadowed `feature` case and for the legacy alias if it can resolve to the custom shadow.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"` currently passes 45 tests, confirming this case is not covered by existing assertions.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateRegistry.cs`
  Evidence: Project-scoped `List()` still loads and validates full custom template bodies through `LoadCustomTemplates()` (`IssueTemplateRegistry.cs:54-60`, `175-197`) and `ValidateTemplate()` still requires every section title/guidance/placeholder (`IssueTemplateRegistry.cs:217-225`). The issue explicitly targets two-stage loading because list previously deserialized every body, and the spec says list SHALL parse metadata only and SHALL NOT parse or return template bodies. The built-in path satisfies this via `IssueTemplateFileLoader.ReadFrontmatterOnly()` (`IssueTemplateFileLoader.cs:80-100`), but the custom path still parses all sections just to render list metadata. This also makes a project custom template with valid metadata but a body/section issue disappear from list even though list does not need the body. [disallowed: product behavior/data contract change]
  SuggestedAction: Split custom template metadata loading from detail loading as well, or explicitly narrow the spec/acceptance criteria to built-ins only. If current DB rows cannot support a cheap metadata-only read, introduce a lightweight metadata DTO/read path that does not validate sections on `List()`, and keep full section validation in `Get()`.
  Verification: Existing tests intentionally assert invalid custom section bodies are not surfaced in list (`IssueTemplateRegistrySpecs.cs:589-764`), which documents the current full-body validation behavior rather than the requested metadata-only behavior.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/IssueTemplate/IssueTemplateApiSpecs.cs`
  Evidence: The acceptance criteria require `GET /api/issue-templates/mohist/default` to return an identical response to `feature`, and the API test only compares id/name/description/section count (`IssueTemplateApiSpecs.cs:81-97`). It does not compare the full `sections` payload or `source`, so regressions in parsed section guidance/placeholder/order or alias source would pass. The registry-level test compares only the first section (`IssueTemplateRegistrySpecs.cs:243-258`), and the CLI test uses mocked server data, not the real API parser.
  SuggestedAction: Add an API-level deep equality assertion between the full `data` payload for `feature` and `mohist/default`, or compare every section title/guidance/placeholder/source explicitly.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"` passes 45 tests with the current weaker alias assertions.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateFileLoader.cs`
  Evidence: `ReadFrontmatterOnly()` reads until EOF if the opening `---` has no matching closing delimiter (`IssueTemplateFileLoader.cs:80-100`). For controlled built-in assets this is unlikely, but it weakens the frontmatter-only guarantee on malformed files.
  SuggestedAction: Consider capping discovery reads at the closing delimiter contract and failing discovery immediately when a frontmatter block is unterminated, without scanning the whole body.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: dependency hygiene
  Evidence: During `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"`, the server project build ran npm and reported `9 vulnerabilities (3 moderate, 3 high, 3 critical)`. This appears unrelated to the issue-template change and did not fail the command.
  SuggestedAction: Triage npm audit findings separately.
  Status: out-of-scope

## Verification Summary

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueTemplate"` — passed, 45 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter "FullyQualifiedName~CliIssueTemplateCommandSpecs"` — passed, 19 tests.
- `npm run typecheck -w packages/web` — passed.
- `npm run test:run -w packages/web -- --run src/entities/issue-templates/api/client.test.ts src/entities/issue-templates/api/queries.test.ts src/features/create-issue/ui/CreateIssueDialog.test.tsx` — passed, 35 tests.
- `npm run test:run -w packages/web` — passed, 198 files, 2988 tests passed, 1 skipped.
- `npm test` — first run timed out at 120s during .NET tests; rerun with a 300s timeout passed. Saved output showed .NET passed 3135 tests with 13 skipped, Web passed 2988 tests with 1 skipped, and runner passed 786 tests with 23 skipped.

<promise>FAIL</promise>
