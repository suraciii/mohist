## Context

Workflow profiles currently carry no descriptive metadata. The only profile is `mohist/default`, defined by `mohist-default.workflow.yaml`, wrapped by `MohistDefaultIssueWorkflowProfile`, and listed via `IssueWorkflowProfileRegistry`. The `WorkflowDefinition` domain type has no `Description` field. The `WorkflowYamlSerializer` uses `YamlDotNet` with `IgnoreUnmatchedProperties()` so new YAML keys won't break parsing.

The issue body and specs converge on a description-only design: one `description` field (YAML block scalar, natural language). No structured fields (`risk_level`, `suitable_for`, etc.). The description is passive metadata — read by humans and AI, ignored by the workflow engine.

## Goals / Non-Goals

**Goals:**
- Add `description` as a first-class top-level field in workflow profile YAML
- Expose description through the server profile listing API and model
- Provide `mo workflow list` CLI command (human and `--json` output)
- Write a complete, AI-readable description for `mohist/default`
- Create `quick-fix` and `experiment` profiles with distinct descriptions
- Update Web UI `WorkflowProfilesSection` to render multi-line descriptions prominently

**Non-Goals:**
- No structured metadata fields (risk_level, suitable_for, tags, etc.)
- No AI selection logic (another issue)
- No profile include/extends mechanism
- No YAML-driven profile registration (new profiles are class-based)

## Decisions

### 1. Add `Description` to `WorkflowDefinition`

Add an optional `string? Description` property to the `WorkflowDefinition` record. Parse it in `WorkflowYamlSerializer.FromYaml` from the `description` YAML key and emit it in `ToYaml`. The workflow engine already treats `WorkflowDefinition` as a data bag and ignores non-execution fields (like `Name`).

**Alternative considered:** Store description outside `WorkflowDefinition`, read YAML raw at profile-registration time. Rejected because it duplicates YAML handling and the description logically belongs with the definition file — it's the YAML that is the artifact, and the serialization should be round-trip.

### 2. New profiles as `IIssueWorkflowProfile` classes sharing the same stages

`quick-fix` and `experiment` reuse `MohistWorkflow.Definition` (the same stages as `mohist/default`) and differ only in their `Id`, `DisplayName`, and `Description` properties. Their descriptions are class-level constants, not separate YAML files. This avoids introducing profile inheritance before it's needed.

**Alternative considered:** Create separate `.workflow.yaml` files per profile with full stage duplication. Rejected because it violates the requirement that profiles differ only in metadata and bloats maintenance.

### 3. Extend existing system templates endpoint with `isDefault` and multi-line descriptions

Extend `SystemTemplateInfo` with an `IsDefault` boolean field and update `ProjectWorkflowProfileManager.SystemTemplates` to carry it. The existing `GET /api/workflow-templates/system` endpoint already serves as the profile list (the frontend `useWorkflowProfiles` already calls it). Updating the existing response type avoids a new endpoint and avoids surprising the frontend with a different contract. The frontend's `getWorkflowProfiles()` can drop the `template.id === 'mohist/default'` heuristic in favor of the server-provided `isDefault` field.

**Alternative considered:** Create a new `GET /api/workflow-profiles` endpoint. Rejected because the existing endpoint already fulfills this role; a new endpoint would require frontend migration with no user-visible benefit.

### 4. CLI `mo workflow list` as thin client

The CLI calls `GET /api/workflow-templates/system`. No YAML parsing in the CLI. Human output uses the existing CLI formatting conventions (colored names, indented descriptions). `--json` outputs verbatim server JSON. Follows the same pattern as `mo skills list`.

### 5. Web UI description rendering with `whitespace-pre-line`

The `ProfileCard` already shows `profile.description` via `line-clamp-2`. In detail view, `ProfileDetail` shows `profile.description` as a single `<p>`. Change to render with `whitespace-pre-line` to preserve line breaks from the multi-line description. Keep the detail view layout: description at top, stages summary below, YAML viewer at bottom.

## Risks / Trade-offs

- **[Risk] Description in WorkflowDefinition could be confused with execution metadata** -> Mitigation: Description is nullable and named unambiguously. The workflow engine has zero references to it; it's consumed only at the profile/API layer.
- **[Risk] quick-fix/experiment descriptions become stale vs. actual stage definitions** -> Mitigation: Descriptions reference general categories ("simple fixes", "experimentation") rather than specific stage names. If stages change, descriptions remain valid.
- **[Trade-off] No YAML per profile for quick-fix/experiment** -> Pro: Simpler, less duplication. Con: Can't round-trip serialization for those profiles. Acceptable since these profiles aren't user-editable — they're reference/fixture profiles for AI selection validation.

## Migration Plan

1. Deploy: Add `description` to `mohist-default.workflow.yaml`, add `Description` to `WorkflowDefinition`, register new profiles, extend system templates endpoint with `isDefault`, add CLI command, update Web UI.
2. Rollback: Old clients ignore the new `description` YAML key (`IgnoreUnmatchedProperties()`). Old Web UI ignores the extra text in the description field. The `description` column in API responses is additive — no existing contract is removed.
3. No data migration needed — profiles are code-defined, not database-stored.

## Open Questions

- None. The design follows the issue body's description-only constraint and existing codebase conventions.
