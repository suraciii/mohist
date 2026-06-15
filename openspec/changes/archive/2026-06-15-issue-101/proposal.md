## Why

Workflow profiles are the product's differentiator but are currently anonymous YAML with no metadata. Users can't make informed choices between profiles, and external skills can't intelligently recommend which workflow fits an issue. The design is "description-only": each profile gets a natural-language `description` field that both humans and AI read to decide which profile to use.

## What Changes

- Workflow profile YAML gains a first-class `description` field (block scalar, natural language)
- `mo workflow list` CLI command outputs each profile's name + description (human-readable and `--json` for skill consumption)
- `mohist/default` profile gets a complete, AI-readable description covering scope, typical behavior, and exclusions
- Two new example profiles (`quick-fix`, `experiment`) with distinct descriptions to validate AI-driven selection
- Web UI `WorkflowProfilesSection` renders each profile's description prominently (not just the YAML editor)
- Backend `WorkflowProfileInfo` and API responses carry the multi-line description
- Profiles without a `description` field default to a sensible fallback

## Capabilities

### New Capabilities

- `workflow-profile-metadata`: The YAML schema extension for the `description` field, plus server-side model, API, and default-value handling
- `cli-workflow-list`: The `mo workflow list` CLI command with human and JSON output modes

### Modified Capabilities

- `web-ui`: `WorkflowProfilesSection` must render each profile's full description (not just one-liner) while keeping the YAML definition as secondary detail

## Impact

- **Server**: `IIssueWorkflowProfile.Description` moves from inline one-liner to a block-scalar sourced from the YAML. `IssueWorkflowProfileRegistry.List()` returns richer metadata. New API endpoint or field for profile list with descriptions.
- **CLI**: New `mo workflow list` command group (thin client, calls server API).
- **Web UI**: `WorkflowProfilesSection` and `ProfileDetail` components updated to render multi-line descriptions.
- **YAML profiles**: `mohist-default.workflow.yaml` gains `description` field. `quick-fix` and `experiment` are class-based profiles sharing the same stage definitions.
- **Backward compatible**: Profiles without `description` default to a reasonable fallback; existing pipelines are unaffected.
