# Review Findings

## F1: Native workflow-profile select remains on the issue-detail page

- **Severity:** Must fix before merge
- **Location:** `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx:129-145`, rendered from `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:589-597`
- **Problem:** The change migrates only the three session filters to the shared `Select`, but the issue-detail reference rail still mounts `WorkflowProfileControl`, whose workflow-profile control is a native `<select>`. This means the page still renders a native select and fails the issue acceptance criterion that all selects on the page use the shared component. It also violates `openspec/changes/issue-457/specs/issue-detail-shared-selects/spec.md`'s page-wide requirement, not just its three session scenarios.
- **Required correction:** Replace this workflow-profile `<select>` and its `<option>` children with the shared select component while preserving the existing value, disabled/locked behavior, change handler, accessible label, and `issue-workflow-profile-select` test hook. Add coverage that renders the issue-detail rail and asserts no native `<select>` remains.

<promise>FAIL</promise>
