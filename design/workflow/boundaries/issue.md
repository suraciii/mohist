# Workflow ↔ Issue

## Rule

`Issue → Workflow` only. Workflow never knows "issue." It operates on abstract runs, `WorkflowDefinition`, and variables.

## What goes where

| Concept | Belongs to | Status |
|---|---|---|
| `WorkflowDefinition` (type + engine) | Workflow | ✓ in `Workflow/Domain/Definition/` |
| workflow profile (template + variables) | Issue / Project | ✓ correct (their config) |
| prompts | Project Space | `prompt-management.md` |
| default `WorkflowDefinition` content (yaml) | application config (composition root) | ❌ wrongly in `Issue/Services/WorkflowProfiles/` |
| projection (attention, etc.) | Issue | ✓ read-only consumption |

## Fix needed

Move `MohistWorkflow.cs` + `mohist-local.workflow.yaml` to application config layer. This removes the only cross-context reverse dependency (`ProjectWorkflowProfileManager` → `Issue.Services.WorkflowProfiles`).
