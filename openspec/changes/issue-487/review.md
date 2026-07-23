# Review Findings

## P2: Existing PR-check recovery contract is no longer asserted

`packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:358-361` still verifies the `recover:fix-pr-checks` task ID and ordering, but the change removed the previous assertions that its prompt is `${{ prompts.fix-pr-checks }}` and its session is `check`. Those values are part of the existing check-stage recovery behavior and are easy to regress while editing this workflow. Restore the assertions so the profile test continues to protect the repair path required by the workflow.

## P2: New profile test does not prove that the synchronization task is the rebase action

`packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:331-342` checks the new task's inputs and conflict handler, but never asserts `rebase.Uses == "mohist/rebase"` (nor the configured agent options/session on its resolver). A task using a different action could therefore satisfy the test while no repository-base synchronization occurs. Assert the action identity and the relevant resolver configuration alongside the existing input/order checks.

<promise>FAIL</promise>
