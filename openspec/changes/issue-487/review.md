# Review Findings

## P2: Integrate recovery contracts were removed from profile coverage

`packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:435-443` now checks only that integrate's base-moved rebase conflict handler contains one task, and `:453-456` checks only the failing-check recovery task IDs. The change removed the previous assertions that these tasks use `mohist/opencode`, run in the `integrate` session, and use `${{ prompts.resolve-rebase-conflicts }}` or `${{ prompts.fix-pr-checks }}`. Retaining integrate's base-moved and branch-protection recovery is an explicit acceptance criterion, so restore those assertions to keep the unchanged final merge protection protected by the profile test.

<promise>FAIL</promise>
