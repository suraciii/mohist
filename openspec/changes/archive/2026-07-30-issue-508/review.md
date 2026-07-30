# Review: Issue 508

## Result

The provider-backed Profile collection now resolves explicit and default custom
Profiles consistently across primary and child Issue projections. Legacy
template resolution and mixed Profile managers remain absent from runtime
services, and the Definition resolver stays separate from variables and prompts.

`IssueQuerierSpecs.ListAsync_ChildIssuesKeepExplicitAndProjectDefaultCustomProfiles`
covers both custom-child paths. The server spec suite passed with 3455 tests.

<promise>PASS</promise>
