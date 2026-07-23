# Self Review: Issue 477

Reviewed the current Issue 477, proposal, design, task breakdown, capability specs, and governing design constraints.

## Findings

1. **[BLOCKER] The terminal WorkflowRun deletion rule is incompatible with the proposed restrictive foreign key unless the plan specifies a lifecycle transition for the backing key.** The issue and collection spec permit deletion when a custom Profile is referenced only by terminal Runs: deletion is blocked by active Runs only (`specs/workflow-profile-collection/spec.md:67-84`). Yet the design requires every custom Run binding to carry a restrictive foreign key (`design.md:63-66`, `design.md:136-143`) and says terminal Runs retain the Profile ID (`design.md:243-245`). A retained restrictive foreign-key reference will make the database reject the delete after the blocker query excludes that terminal Run. Specify that terminalization clears only the nullable custom-Profile backing key while retaining the public Profile ID, define the migration state for already-terminal Runs, and add transactional coverage proving a Profile referenced only by a terminal Run can be deleted without losing Run history.

<promise>FAIL</promise>
