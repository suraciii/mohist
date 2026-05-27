# Specs Artifact

Create specification files that define WHAT the system should do.

## Structure

Create one spec file per capability listed in the proposal's Capabilities section.
- New capabilities: use the exact kebab-case name from the proposal (`specs/<capability>/spec.md`).
- Modified capabilities: use the existing spec folder name from `openspec/specs/<capability>/` when creating the delta spec at `specs/<capability>/spec.md`.

## Delta Operations

Use `##` headers to indicate the operation type:

- **ADDED Requirements**: New capabilities
- **MODIFIED Requirements**: Changed behavior — MUST include full updated content
- **REMOVED Requirements**: Deprecated features — MUST include **Reason** and **Migration**
- **RENAMED Requirements**: Name changes only — use FROM:/TO: format

## Format Requirements

- Each requirement: `### Requirement: <name>` followed by description
- Use SHALL/MUST for normative requirements (avoid should/may)
- Each scenario: `#### Scenario: <name>` with WHEN/THEN format
- **CRITICAL**: Scenarios MUST use exactly 4 hashtags (`####`). Using 3 hashtags or bullets will fail silently.
- Every requirement MUST have at least one scenario.

## MODIFIED Requirements Workflow

1. Locate the existing requirement in `openspec/specs/<capability>/spec.md`
2. Copy the ENTIRE requirement block (from `### Requirement:` through all scenarios)
3. Paste under `## MODIFIED Requirements` and edit to reflect new behavior
4. Ensure header text matches exactly (whitespace-insensitive)

Common pitfall: Using MODIFIED with partial content loses detail at archive time. If adding new concerns without changing existing behavior, use ADDED instead.

## Example

```
## ADDED Requirements

### Requirement: User can export data
The system SHALL allow users to export their data in CSV format.

#### Scenario: Successful export
- **WHEN** user clicks "Export" button
- **THEN** system downloads a CSV file with all user data

## REMOVED Requirements

### Requirement: Legacy export
**Reason**: Replaced by new export system
**Migration**: Use new export endpoint at /api/v2/export
```

Specs should be testable — each scenario is a potential test case.

**IMPORTANT**: Project context and rules from the prompt are constraints for you. Do NOT copy them into the output file.

## Output

Write each spec file to `${{ openspecChangeDir }}/specs/<capability>/spec.md`.
