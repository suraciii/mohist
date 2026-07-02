## Why

`mohist/local` resolves its user-facing description from its workflow YAML at runtime (`MohistLocalIssueWorkflowProfile.ResolveDescription()` → `MohistWorkflow.Definition.Description`), making the YAML the single source of truth. `mohist/github-pr` does not: it shows a string hardcoded as a C# `const` (`MohistGithubPrIssueWorkflowProfile.GithubPrDescription`, compiled into the binary — any copy edit forces a rebuild), while the `description` parsed from `mohist-github-pr.workflow.yaml` into `WorkflowDefinition.Description` is never read and is dead code. The two copies have already drifted in wording (the const says "auditability on GitHub matters", the YAML says "traceable GitHub PR record per issue"), so editing copy requires first remembering which profile reads from where. This collapses the duplication so the YAML is the one source for both built-in profiles.

## What Changes

- `MohistGithubPrIssueWorkflowProfile.Description` is resolved from `MohistWorkflow.GithubPrWorkflowDefinition.Description` (the parsed YAML) via the same empty-fallback pattern as `MohistLocalIssueWorkflowProfile.ResolveDescription()`, instead of the C# constant.
- The `public const string GithubPrDescription` constant and the `TrimEnd()` call that referenced it are removed from `MohistGithubPrIssueWorkflowProfile`.
- `ProjectWorkflowProfileManager.BuildSystemTemplates()` builds its github-pr `SystemTemplateInfo.Description` from `GithubPrWorkflowDefinition.Description` (with the same empty fallback) instead of referencing `MohistGithubPrIssueWorkflowProfile.GithubPrDescription`; the local and github-pr branches now assemble the description identically.
- The displayed github-pr description becomes the YAML `description` text (which already carries the `gh` / `gh auth login` prerequisite and "GitHub PR" wording the existing specs assert on); the stale C# wording is retired.
- Specs that asserted against the `GithubPrDescription` constant directly are rewritten to assert against the YAML-sourced description (profile `Description` + `SystemTemplateInfo.Description`).

## Capabilities

- `workflow-profile-description`: How the built-in system workflow profiles (`mohist/local`, `mohist/github-pr`) resolve the user-facing description surfaced through `IIssueWorkflowProfile.Description` and `ProjectWorkflowProfileManager`'s `SystemTemplateInfo.Description` — the workflow YAML `description` is the single source of truth for both, with a consistent empty-value fallback, and no parallel compiled string constant.

## Impact

- **Server** (`packages/server/src/Mohist.Server`):
  - `Issue/Services/WorkflowProfiles/MohistGithubPrIssueWorkflowProfile.cs` — delete `GithubPrDescription` const; `Description` reads `GithubPrWorkflowDefinition.Description` with empty fallback (mirrors `MohistLocalIssueWorkflowProfile`).
  - `Workflow/Services/ProjectWorkflowProfileManager.cs` (`BuildSystemTemplates`) — github-pr branch reads `GithubPrWorkflowDefinition.Description` instead of the deleted constant; both branches now share the same assembly pattern.
  - `Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml` — already the authoritative description; no parsing-logic change (Non-Goal).
- **Tests** (`packages/server/tests/Mohist.Server.Tests`):
  - `Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs` — the `..._AsConstant` case (asserts the const symbol) is removed/rewritten to assert the YAML-sourced `profile.Description` and `SystemTemplateInfo.Description`; existing substring assertions (`gh`, `gh auth login`, `GitHub PR`) keep passing because the YAML carries those tokens.
- **No impact**: no API contract change, no schema migration, no runner/web/CLI change, no `WorkflowYamlSerializer` parsing change (Non-Goals). risk=low — single subsystem (description assembly), no external contract delta.
