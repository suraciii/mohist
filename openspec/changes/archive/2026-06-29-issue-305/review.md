# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs:30
  Evidence: `mo project workflow config get -o json` returns immediately after `GET /workflow-profile` and never fetches `/workflow-profile/prompts`. The server route at `packages/server/src/Mohist.Server/Api/ProjectRoutes.cs:225` returns only `projectId`, `defaultTemplateId`, and `variables`; prompts are a separate route at `ProjectRoutes.cs:338`. The issue acceptance criterion requires viewing project workflow variables and prompt overrides from CLI, and the spec says `config get` renders the complete project-level override state in one view and supports `-o json` raw payload (`openspec/changes/issue-305/specs/cli-interface/spec.md:198`). The existing issue-level helper also merges prompts before both JSON and table output (`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:1270`). The new test `ConfigGet_JsonMode_EmitsRawPayload` asserts the incomplete behavior by checking `Assert.DoesNotContain("plan_prompt", stdout)` at `packages/cli/tests/Mohist.Cli.Tests/CliProjectWorkflowCommandSpecs.cs:576`. [disallowed:product behavior change]
  SuggestedAction: Fetch `/workflow-profile/prompts` for JSON mode as well, merge it into the profile payload under `prompts`, and update the JSON-mode test to assert prompt overrides are included.
  Verification: Run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true`; manually or via test assert that `mo project workflow config get -o json` includes `defaultTemplateId`, `variables`, and `prompts`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/cli/Mohist.Cli/TableRenderer.ProjectWorkflow.cs:50
  Evidence: The project template show renderer reads `definition` as a string, but the real server returns a `WorkflowDefinition` object from `GET /api/projects/{project}/workflow-templates/{tid}` (`ProjectRoutes.cs:186` returns `definition = def`). `TableRenderer.StringOf` intentionally returns an empty string for `JsonObject` values (`TableRenderer.cs:266`), so `mo project workflow template show <id> -o table` will omit the template definition against the real API. This violates the acceptance criterion "可通过 CLI ... 查看 ... workflow 模板" and the spec requirement that table output present the template definition (`openspec/changes/issue-305/specs/cli-interface/spec.md:142`). The CLI test masks the mismatch by using `definition = SampleTemplateDefinition` as a string in `CliProjectWorkflowCommandSpecs.cs:38`, which does not match the server contract. [disallowed:product behavior change]
  SuggestedAction: Render object definitions correctly, for example by serializing the `definition` node as formatted JSON or adding a server-compatible DTO expectation; update tests to use an object-shaped `definition` matching `ProjectRoutes.cs:190`.
  Verification: Run the focused CLI tests and add a test where `definition` is a JSON object; confirm table output includes the definition content for `mo project workflow template show`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:749
  Evidence: Changing `archive`'s issue number positional to optional for `--all-completed` creates a regression for plain `mo issue archive`. With neither `<number>` nor `--all-completed`, the command passes `number!` into `$"/issues/{Uri.EscapeDataString(number!)}/archive"` at `MohistCliCommands.Issue.cs:805`, so it can throw or construct an invalid path instead of producing the previous required-argument validation. This is outside the new batch flag happy path but is caused by the current change. [disallowed:product behavior change]
  SuggestedAction: Add an explicit validation branch: if `!allCompleted && string.IsNullOrWhiteSpace(number)`, print a clear message that `<number>` is required unless `--all-completed` is used and exit non-zero without calling the API. Add a regression test for `mo issue archive` with no arguments.
  Verification: Run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true`; manually confirm `mo issue archive` exits cleanly without an exception or HTTP request.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/cli/tests/Mohist.Cli.Tests/CliProjectWorkflowCommandSpecs.cs
  Evidence: The new CLI tests do not cover the real server DTO shape for project template `show`: they model `definition` as a string (`CliProjectWorkflowCommandSpecs.cs:38`) while `ProjectRoutes.cs:190` returns a structured `WorkflowDefinition` object. This allowed item-2 to pass the focused CLI test suite while the real table output would be incomplete.
  SuggestedAction: Change the fixture to use an object-shaped workflow definition or add an additional server-shaped test, and assert the renderer displays meaningful definition content.
  Verification: Run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true` and confirm the new test fails before the renderer fix and passes after it.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: docs/cli-reference.md:149
  Evidence: The quick-reference line `mo issue session followup <number> <name> --text <text|--text-file|--text-stdin>` is misleading because `--text-file` and `--text-stdin` are alternative flags, not values to `--text`. The following table documents the alternatives correctly, so this is a documentation polish issue rather than a blocker.
  SuggestedAction: Rewrite the example as `mo issue session followup <number> <name> --text <text>` and rely on the table for file/stdin variants, or show three separate examples.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: verification
  Evidence: `npm test -w packages/cli` and `npm run typecheck -w packages/cli` fail with `No workspaces found: --workspace=packages/cli` because the root `package.json` only lists `packages/web` and `packages/runner` as npm workspaces. The CLI is a .NET project, so this is a command-selection/documentation mismatch rather than a product defect in the reviewed change.
  SuggestedAction: Use `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true` for focused CLI verification, or document a root script for CLI tests.
  Status: out-of-scope

- [ID: item-7]
  Severity: warning
  Scope: verification
  Evidence: `npm test` started repository-level tests but exceeded the 120 second review timeout while server tests were still running. Focused CLI verification passed: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true` passed 515 tests in 784 ms.
  SuggestedAction: Re-run `npm test` with a longer timeout before integration, especially after fixing the blocking CLI contract issues.
  Status: out-of-scope

<promise>FAIL</promise>
