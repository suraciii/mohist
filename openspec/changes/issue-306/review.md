# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: cleanup
  Evidence: `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/ProjectWorkflowProfileRow.cs:27` still documented `DisableDefaultIssueTemplate` as disabling only the legacy `mohist/default` template, while the accepted implementation gates all built-in issue templates. Updated the XML summary to say it disables built-in issue templates generally.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed after the edit: 3131 passed, 13 skipped.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateFileLoader.cs`, `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateRegistry.cs`
  Evidence: The candidate does not implement the required two-stage on-demand loading model. `IssueTemplateRegistry` constructs `IssueTemplateFileLoader` and calls `_builtinData = loader.Load()` during registry construction at `IssueTemplateRegistry.cs:21-23`. `IssueTemplateFileLoader.Load()` then reads every template with `File.ReadAllText(filePath)` and calls `PromptFrontmatterParser.Parse(content, id)` at `IssueTemplateFileLoader.cs:26-27`, storing the returned `body` in `BuiltinTemplateEntry` at `IssueTemplateFileLoader.cs:32-35`. As a result, the body for every built-in template is read and split before any `List()` or `Get()` call. This violates the issue acceptance criterion that `list` only parse frontmatter and `get` parse body, and the spec requirement that `list` not pay the cost of parsing every template body. [disallowed:reason] Fixing this requires changing the loader/registry contract, not a safe local review repair.
  SuggestedAction: Store template file paths plus frontmatter metadata from discovery, and defer reading/parsing the markdown body until `Get()` for the requested id. Add an observable test using a fake file store or equivalent seam proving `List()` does not read/parse body content and `Get()` does.
  Verification: Re-run `dotnet test Mohist.sln -p:SkipWebBuild=true`; add a failing-then-passing regression test that detects body reads on `List()`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateRegistry.cs`
  Evidence: A project with `DisableDefaultIssueTemplate=true` can list a custom template whose id collides with a built-in id, but cannot fetch it. `List()` skips built-ins when disabled, then appends custom templates at `IssueTemplateRegistry.cs:45-60`. `Get()` checks `_builtinData.TryGetValue(resolvedId, out var entry)` first, and if the id is a built-in while defaults are disabled it throws at `IssueTemplateRegistry.cs:71-74` before checking project custom rows at `IssueTemplateRegistry.cs:80-84`. So a custom row named `feature`, `bug`, or `refactor` is returned by `List(projectId)` but `Get("feature", projectId)` fails. This conflicts with the design intent that disabling built-ins leaves the project seeing only its custom templates, and creates a list/get inconsistency for a valid custom-template row. [disallowed:reason] Resolution requires product behavior precedence between custom and disabled built-in ids.
  SuggestedAction: Decide and encode id precedence explicitly. If custom templates are allowed to shadow built-ins when built-ins are disabled, check project custom rows before throwing the disabled-built-in error. If collisions are disallowed, filter or reject them consistently so `List()` and `Get()` agree.
  Verification: Add a registry/API regression test with `DisableDefaultIssueTemplate=true` and a custom row named `feature`, asserting the chosen list/get behavior.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/IssueTemplate/IssueTemplateRegistrySpecs.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/IssueTemplate/IssueTemplateApiSpecs.cs`
  Evidence: The tests cover the happy-path shape, aliases, body parser output, and disabled built-in filtering, but they do not verify the key lazy-loading contract or the disabled-builtin/custom-collision case. The existing registry specs inject `Dictionary<string, BuiltinTemplateEntry>` directly at `IssueTemplateRegistrySpecs.cs:36-41`, so they bypass `IssueTemplateFileLoader` and cannot catch that `Load()` eagerly reads and stores every body. The API specs assert response shape but not whether list avoided body reads.
  SuggestedAction: Add a focused loader/registry test double that can fail if body content is accessed during list, and add the custom collision test described in item-3.
  Verification: Run `dotnet test Mohist.sln -p:SkipWebBuild=true` and ensure the new tests fail on the current candidate before the implementation is corrected.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateFileLoader.cs`
  Evidence: Missing or invalid template asset directory failures currently escape from `Directory.EnumerateFiles(_directory, "*.md")` or `PromptFrontmatterParser.Parse(...)` during scoped registry construction. That is acceptable for surfacing a broken deployment, but the error message will be a low-level IO/YAML exception without issue-template context.
  SuggestedAction: Consider wrapping loader failures with template-directory/id context once the lazy-loading contract is corrected.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: verification
  Evidence: The first top-level `npm test` invocation timed out after 120s before reaching all workspace test phases. The phases were rerun separately: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed, `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true` passed, `npm run typecheck -w packages/web` passed, `npm run test:run -w packages/web` passed, and `npm run test:ci --workspaces --if-present` passed.
  SuggestedAction: No product action required.
  Status: out-of-scope

<promise>FAIL</promise>
