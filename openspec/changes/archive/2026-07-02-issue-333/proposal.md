## Why

The workflow-profile descriptive metadata has two coupled drift problems. First, the two built-in profiles source their user-facing description differently: `mohist/local` reads it from `mohist-local.workflow.yaml`, but `mohist/github-pr` reads it from a hardcoded C# string constant (`MohistGithubPrIssueWorkflowProfile.GithubPrDescription`) compiled into the binary — two sources of truth that have already diverged in wording, where changing the GitHub PR text forces a recompile. Second, the profile `SuitableFor` structured tag list is redundant: the natural-language `description` already states applicability, the field has zero production consumers (`registry.Matches()` / `SuitableForMatcher` are dead code; Web, runner, and DB never read it), and it survives only as an extra surface that can drift. Both are noise in a single subsystem with no persisted state.

## What Changes

- **Single description source from YAML.** Every system workflow profile's user-facing description SHALL be read from the workflow YAML `description` field, exactly as `mohist/local` already does. `MohistGithubPrIssueWorkflowProfile.Description` SHALL read `MohistWorkflow.GithubPrWorkflowDefinition.Description` (with the same empty-value fallback as local), and `ProjectWorkflowProfileManager.BuildSystemTemplates()` SHALL source both profiles' descriptions from `WorkflowDefinition.Description` identically.
- **Delete the C# description constant.** `MohistGithubPrIssueWorkflowProfile.GithubPrDescription` and every reference to it are removed.
- **BREAKING (API field):** Remove `SuitableFor` from the workflow-profile model — the `IIssueWorkflowProfile.SuitableFor` property, the base-class abstract member, both built-in overrides, the `WorkflowProfileDescription` DTO field, the `registry.Matches()` method, and the entire `SuitableForMatcher.cs` file. The `suitableFor` JSON field leaves the `/api/workflow-profiles` response. (No production consumer relies on it.)
- **CLI:** `mo workflow list --described` no longer parses or prints a `Suitable for:` line; only the description is shown. The `--described` option help text drops the "suitable_for context" wording.
- **Skill:** the bundled `mohist-create-issue` skill no longer matches profiles against `suitable_for` tags — workflow selection uses the default profile or operator choice over the description.
- Out of scope: the description wording itself (already corrected manually); `WorkflowYamlSerializer` parsing logic; DB schema (nothing was persisted).

## Capabilities

### New Capabilities

- `workflow-profile-description`: The user-facing descriptive-metadata model for system workflow profiles. The description is the single descriptive field and SHALL be sourced solely from the workflow YAML `description` field (no C# hardcoded constants), read consistently by both built-in profiles (`mohist/local`, `mohist/github-pr`) and both catalog materialization paths (`SystemTemplateInfo` from `BuildSystemTemplates`, `WorkflowProfileDescription` from `ListDescribed`) with an empty-value fallback. The profile model SHALL carry no `SuitableFor` structured field — the natural-language `description` is the sole applicability description — with the `SuitableForMatcher`, `registry.Matches()`, and CLI/skill consumers removed.

### Modified Capabilities

- _(none — workflow-profile description metadata is not currently described by any existing spec. The discovery filtering governed by `workflow-profile-discovery` and the issue→profile resolution governed by `issue-workflow-profile` are unchanged; only the response shape of the same discovery endpoints loses a field, which is new ground captured above.)_

## Impact

- **Server** (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/`):
  - `MohistGithubPrIssueWorkflowProfile.cs` — delete `GithubPrDescription` const; `Description` reads `MohistWorkflow.GithubPrWorkflowDefinition.Description` (with local-style fallback); delete `SuitableFor` override.
  - `MohistLocalIssueWorkflowProfile.cs` — delete `SuitableFor` override.
  - `IIssueWorkflowProfile.cs` / `MohistIssueWorkflowProfileBase.cs` — delete the `SuitableFor` member.
  - `IssueWorkflowProfileRegistry.cs` — drop `SuitableFor` from `WorkflowProfileDescription` and `ListDescribed()`; delete `Matches()`.
  - `SuitableForMatcher.cs` — file deleted.
  - `Workflow/Services/ProjectWorkflowProfileManager.cs` — `BuildSystemTemplates()` sources the github-pr description from `WorkflowDefinition.Description`; drop the constant reference.
- **CLI** (`packages/cli/Mohist.Cli/`): `MohistCliApi.RenderWorkflowProfilesDescribed` drops the `suitableFor` parse and the `Suitable for:` output line; `MohistCliCommands.Workflow.cs` `--described` help text updated.
- **Skill data** (`packages/cli/Mohist.Cli/skill-data/mohist-create-issue/`): workflow-selection guidance no longer relies on `suitable_for` tags.
- **Tests**: `MohistPrIssueWorkflowProfileSpecs.cs` assertions repointed from the C# constant to the YAML-sourced description; SuitableFor-positive assertions (registry `ListDescribed` exposes SuitableFor, profile `SuitableFor` non-empty, gh-cli tag mention) removed or rewritten; CLI/server `--described` and `/api/workflow-profiles` response specs updated to drop `suitableFor`.
- **No DB migration, no runner change, no Web change** — `SuitableFor` was never persisted and never read by Web or runner; the field removal is the only externally visible API delta and has no production consumer.
- `dotnet build Mohist.sln` and the affected specs must pass.
