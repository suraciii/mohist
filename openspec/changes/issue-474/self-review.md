## Findings

### 1. [P1] Run-level archive default would mask explicit Project and Issue values

[design.md](design.md:50) requires `WorkflowRunProfileManager` to ensure `archive: ""` whenever a run is created, while the Variable spec requires Project, Issue, stage, and Run values to retain their established precedence. The plan also states that the resolver deep-merges Project, Issue, then WorkflowRun values ([design.md](design.md:44)); the existing `VariableBundle.MergeAll` implements exactly that last-write-wins order. Consequently, a workflow with `Project.vars.archive = "project-value"` and no explicit Run value would receive the newly seeded Run `archive: ""`, and resolve to the empty value instead of the configured Project value. The same applies to an Issue or selected-stage value.

The design and T-002 need a precise fallback strategy plus tests that distinguish an initialization default from an explicit Run override. It must guarantee `vars.archive == ""` only when no higher-precedence explicit source is present, while preserving explicit Project, Issue, stage, and Run values and later live updates.

<promise>FAIL</promise>
