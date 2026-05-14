## Self Review

Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change-local spec deltas for alignment, completeness, consistency, feasibility, and dependency correctness.

Findings fixed during review:
- Added the missing change-local `specs/cli-interface/spec.md` delta so the proposal's `cli-interface` capability has concrete requirements for stub install, packaged reads, `--full`, `--all`, `path`, and `MOHIST_SKILLS_DIR`.
- Added the missing change-local `specs/mohist-skill-guidance/spec.md` delta so the proposal's `mohist-skill-guidance` capability now captures version-matched packaged guidance and compact installed stubs.
- Updated `tasks.json` so each task references the concrete change-local requirement it implements instead of broad global spec files.
- Removed the resolved design open question about missing specs after adding the delta specs.

Final checks:
- Proposal changes trace to the issue requirements: dynamic packaged reads, stub-only installs, supplementary references, compatibility with existing full installs, and environment override support.
- The change now has spec deltas for every capability listed in the proposal.
- Design decisions align with the specs: dual asset layout, packaged resolver, stub-only install path, deterministic supplementary aggregation, and local CLI behavior.
- Tasks cover every spec area and form a valid DAG: every non-first task has `dependsOn`, every dependency references an existing lower-priority task, and there are no cycles.

<promise>PASS</promise>
