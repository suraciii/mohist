## Self Review

Reviewed proposal, design, spec delta, and tasks for alignment with Issue #213.

Findings addressed during review:

- Added the missing `specs/mohist-skill-guidance/spec.md` delta so the proposal's modified capability has explicit requirements and scenarios.
- Updated `tasks.json` so both implementation tasks reference the new `mohist-skill-guidance` spec requirement instead of an empty spec field.

Validation results:

- Proposal changes trace to the issue requirements and non-goals.
- Design aligns with the Hermes-native installation approach and excludes `skills.external_dirs` configuration.
- Spec scenarios cover full `skill-data` installation, stub exclusion, `HERMES_HOME`, config non-mutation, built-in skill scope, repeat installs, output guidance, and existing install behavior preservation.
- Tasks cover the spec with a valid dependency graph: `T-002` depends on lower-priority `T-001`, with no cycles.
- `tasks.json` parses as valid JSON.

<promise>PASS</promise>
