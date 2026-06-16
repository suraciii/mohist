## Why

Explore-to-issue handoff is a critical input-quality gate: the explore skill produces findings in agent dialogue, but today those findings land in issues as free-form body text with no structured linkage to workflow selection. This forces users to manually choose the right workflow and leads to inconsistent issue quality. We need a structured, low-loss handoff where explore findings include a machine-readable workflow recommendation so runtime can start with the right profile automatically.

## What Changes

- Define an **issue body frontmatter convention** with `recommended_workflow`, `recommended_workflow_reason`, and `risk` fields — advisory, not blocking
- **`mo issue create --body-file`** parses YAML frontmatter from the body file, auto-filling `--workflow-profile` and risk when present
- **`mohist-explore` skill** produces issue body with complete frontmatter + structured sections (Background, Goal, Non-goals, Acceptance criteria), calling `mo workflow list --described` to discover eligible workflows
- **Web UI create-issue dialog** displays the recommended workflow and reason when body content contains frontmatter, with one-click acceptance

## Capabilities

### New Capabilities
- `issue-body-frontmatter`: Structured YAML frontmatter convention for issue bodies carrying workflow recommendation and risk metadata
- `explore-issue-handoff`: Structured handoff protocol where the explore skill produces frontmatter-annotated issue bodies and recommends a workflow

### Modified Capabilities
- `cli-interface`: `mo issue create --body-file` gains frontmatter parsing to auto-populate workflow profile and risk; missing frontmatter warns but does not block
- `web-ui`: Create-issue dialog surfaces recommended workflow and reason from frontmatter, with one-click acceptance
- `mohist-skill-guidance`: Explore skill guidance updated to produce structured frontmatter bodies and call `mo workflow list` for workflow discovery

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`): `--body-file` path gains YAML frontmatter parsing, auto-filling `--workflow-profile` and risk fields
- **Server API** (`POST /api/issues`): Accepts optional `workflowProfileId` and `risk` from create requests (body already supports these via existing issue model)
- **Web UI** (issue create dialog): New frontmatter-aware recommendation display, possible new API query to `GET /api/workflows` for listing
- **Skill** (`.agents/skills/mohist-explore/` and packaged skill data): Updated produce-issue-body workflow with `mo workflow list --described` call
- **Workflow profiles** (`.mohist/workflows/`): Profiles must expose `suitable_for` metadata for matching — prerequisite dependency
