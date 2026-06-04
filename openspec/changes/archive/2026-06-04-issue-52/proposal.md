## Why

The current Issue Detail page always renders the Workflow Profile area as an editable YAML textarea, even when the issue is only inheriting a reference profile and owns no custom YAML. The result is a misleading UI: a large empty editor with a `Loading workflow profile...` placeholder, a disabled `Save` button, and the same `Workflow Profile: mohist/default` identity duplicated between the main card and the DETAILS sidebar. The issue page should clearly tell the user which workflow profile the issue is using and whether it is inherited or customized, and only show the editor when there is real issue-owned YAML to edit.

This needs to be fixed now because the issue page is the primary place users go to understand which workflow will run for an issue, and the current rendering is the most user-visible symptom of a missing UI state model for inherited vs custom profiles.

## What Changes

- Replace the always-visible YAML textarea in the Issue Detail Workflow Profile card with state-aware rendering:
  - **Reference / inherited state** (`yaml: null`, `hasCustomTemplate: false`): render a compact read-only summary listing profile id, mode (`Inherited`), template source (system or project default), and overrides (`None`), plus a `Customize profile` action that opens the editor.
  - **Custom state** (`yaml` present, `hasCustomTemplate: true`): render the YAML editor with a label that clearly says it edits issue-owned workflow profile YAML (not active run YAML), keep existing save / discard / error / validation behavior, and expose a way to revert to the inherited profile.
  - **Loading state**: render the existing skeleton, never the editor placeholder.
  - **Error state**: render a compact error block with a retry affordance, never the editor placeholder.
- Remove the duplicated `Workflow Profile: mohist/default` row from the DETAILS sidebar once the main card carries that identity, keeping DETAILS focused on issue metadata (stage, project, repository, etc.).
- Keep the existing `Coder Model` and per-stage override controls in the ACTIONS sidebar unchanged — this issue is not a workflow variable / model configuration redesign.
- If active run YAML is shown on the issue page (via the existing `WorkflowYamlDialog`), keep labeling it as runtime output / observation, not as workflow profile configuration.
- Add Web tests for the four states (reference, custom, loading, error) and for the sidebar de-duplication.
- The backend already returns `profileId`, `updateMode`, `hasCustomTemplate`, and `yaml: string | null`; no schema or workflow runtime change is required. If a small read-model field is needed to label the inherited source more accurately (e.g., `templateSource` of `system` vs `project`), add it to the existing workflow-profile read endpoint rather than introducing a new contract.

## Capabilities

### New Capabilities
- `issue-workflow-profile-ui`: State-aware rendering of the Issue Detail Workflow Profile card, including inherited vs custom modes, loading and error affordances, editor labeling, and the relationship between the card and the DETAILS sidebar.

### Modified Capabilities
- `web-ui`: Existing Issue Detail rendering requirements remain in force; this change adds the Workflow Profile state model to the Issue Detail page, removes the duplicate `Workflow Profile` row from the DETAILS sidebar, and adds regression coverage for the new states. A delta spec will be added to record the new requirement.

## Impact

Affected code:
- `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx` — primary refactor target; switches from a single textarea view to a state-routing view (reference summary, custom editor, loading skeleton, error block).
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx` — drops the duplicated `Workflow Profile` row from the DETAILS sidebar (around line 724-731); ensures the Workflow Profile card is rendered as a single source of truth.
- `packages/web/tests/IssueWorkflowProfileEditor.test.tsx` — existing tests cover the custom-mode editor flows; expand to cover reference state (no textarea, `Customize profile` action), loading state (skeleton, no placeholder), and error state (retry affordance).
- Optional, only if a small read-model field is needed: `packages/web/src/entities/issue/api/types` and the corresponding backend workflow-profile read endpoint, to surface a friendly inherited-source label (e.g., `system` vs `project`).

Affected APIs / contracts:
- `GET /projects/:projectId/issues/:number/workflow-profile` response — may gain a small optional field (e.g., `templateSource`) to label inherited sources accurately. No breaking change.
- Issue API and workflow runtime behavior are unchanged.

Affected UX surfaces:
- Issue Detail page only. Kanban cards, Epic detail, and other surfaces are not touched by this change.

Non-goals (explicitly out of scope for this proposal):
- Building a separate full workflow configuration sub-page.
- Redesigning project-level workflow variables or per-stage model configuration.
- Adding an effective variables preview.
- Making active run YAML editable.
- Changing workflow template / variable merge semantics.
