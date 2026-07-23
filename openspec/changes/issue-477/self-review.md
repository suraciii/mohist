# Self Review: Issue 477

Reviewed the current issue, proposal, design, task breakdown, and all three capability specs.

## Findings

1. **[BLOCKER] The proposal permits a legacy command behavior that the issue and remaining plan explicitly forbid.**
   `proposal.md` line 26 says legacy Profile-management commands are "removed or redirected to the new owned surfaces." A redirect leaves the old command surface available, contradicting the issue's requirement that `mo workflow` is the only Profile-management object, the collection spec's sole-surface requirement, and `design.md` lines 141-156, which explicitly rejects aliases and requires old groups, routes, DTOs, and tests to be removed. Resolve this by requiring removal, rather than redirection, consistently in the proposal and task acceptance criteria.

<promise>FAIL</promise>
