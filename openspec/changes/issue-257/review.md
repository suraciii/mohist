# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs
  Evidence: Startup can still use a workflow definition that disagrees with the displayed effective profile. `EffectiveWorkflowProfileResolver` correctly defines issue selection precedence over project default in `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/EffectiveWorkflowProfileResolver.cs:45`, and issue/detail/list projection uses that resolver. However `WorkflowProfileManager.LoadTemplateAsync` returns the project default template before resolving the issue effective profile in `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:88`. The added test explicitly locks in the conflicting behavior: an issue with `issueWorkflowProfileId: "mohist/pr"` and project default `mohist/default` is expected to load `system-template:mohist/default` in `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Querier/WorkflowProfileManagerSpecs.cs:198`. This violates the issue invariant and acceptance criterion that startup uses the same profile the user sees; the issue read model would show `mohist/pr` while startup runs default. [disallowed:product-behavior-change]
  SuggestedAction: Change startup/template resolution so explicit issue-level `WorkflowProfileId` wins over project default when no custom YAML or explicit issue template override is present, and update the regression test to assert the PR template in this scenario.
  Verification: Add/run a test where project default is `mohist/default`, issue selection is `mohist/pr`, `GET /issues/:n` reports `mohist/pr`, and `/workflow-runs/:id/yaml` contains PR publish/merge tasks and not default rebase tasks.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/web/src/widgets/kanban-board/ui/IssueCard.tsx
  Evidence: The issue list card only renders a workflow profile chip when `issue.workflowProfileId` is truthy in `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:272`, and the new test asserts that no chip renders for `workflowProfileId: null`. The acceptance criteria require the issue list to display the same effective profile fact as detail/API/workflow-profile; when no issue-level selection exists the server read model should resolve to `mohist/default`, not hide the value. If any list item reaches the UI with `null` or during stale/partial data, the list diverges from the detail control which falls back to `mohist/default`. [disallowed:product-behavior-change]
  SuggestedAction: Render the effective profile on list cards consistently, falling back to the same default/profile endpoint semantics as the detail control or relying on the server read model always supplying a resolved non-null value; update the test to expect the inherited default rather than absence.
  Verification: Run Web tests covering backlog cards for explicit `mohist/pr` and inherited `mohist/default`, and verify issue detail, workflow-profile page, and list display the same value.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/runner/tests/acp-agent.spec.ts
  Evidence: The candidate deletes `packages/runner/tests/acp-agent.spec.ts` entirely according to `git diff --name-status master...HEAD`; this is outside the workflow-profile product area and removes ACP agent regression coverage without replacement in the reviewed change. The candidate also modifies `packages/runner/tests/runner-host.spec.ts`, but that does not replace the deleted ACP agent behavioral coverage. [disallowed:broad/unrelated-test-coverage-change]
  SuggestedAction: Restore the deleted runner ACP test file or provide an equivalent replacement that preserves the removed coverage; keep unrelated runner test cleanup out of this workflow-profile change unless it is required and documented.
  Verification: Run `npm test -w packages/runner` and confirm ACP agent behavior remains covered.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx
  Evidence: New files lack trailing newlines, as shown by the diff markers for `WorkflowProfileControl.tsx`, `WorkflowProfileControl.test.tsx`, and several new C# tests. This is not behavioral, but it is easy to clean up before integration.
  SuggestedAction: Run the repo formatter or add final newlines to new source/test files.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: CodeGraph
  Evidence: CodeGraph is not initialized in this workspace, so structural review used direct diff, grep, and file reads instead.
  SuggestedAction: Optionally initialize CodeGraph for future large cross-cutting reviews.
  Status: out-of-scope

<promise>FAIL</promise>
