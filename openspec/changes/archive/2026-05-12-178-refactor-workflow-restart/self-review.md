Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change-local `specs/` deltas for alignment, completeness, consistency, feasibility, and dependency correctness.

Findings addressed during self-review:

- `tasks.json` originally referenced change-local spec files and anchors that did not exist yet.
- The change-local `specs/` directory was empty, so proposal capabilities, design decisions, and tasks had no concrete spec contract tying them together.

Fixes applied:

- Added change-local spec delta files for `reopen-resume`, `http-api`, `cli-interface`, and `web-ui`.
- Chose requirement headings whose Markdown anchors match the `spec` references already used in `tasks.json`.
- Rechecked dependency rules in `tasks.json`: every non-first task has `dependsOn`, all references point to earlier existing tasks, and the graph is acyclic.

Residual notes:

- The first implementation task intentionally publishes the spec deltas even though they now exist, because autonomous execution still benefits from an explicit “stabilize/finalize spec contract” task. This is slightly redundant but not inconsistent.
- The design keeps `rewind` as future-facing guidance only, which matches the proposal non-goals and the current task split.

<promise>PASS</promise>
