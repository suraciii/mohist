## Self-Review

- Fixed proposal completeness by filling the previously empty Capabilities section so the intended shared runtime and modified runner behavior are explicit.
- Added `specs/workflow-engine/spec.md` to cover the issue scope: shared `StageContext` emit/log helpers, minimal shared handler contract, static task loading, legacy repair/fix adapter compatibility, `AgentSessionTaskHandler`, and `ServiceCallTaskHandler`.
- Updated `tasks.json` so every task now references the spec requirement it implements, and added `"version": 1` to match current task artifact structure.
- Corrected `design.md` so it no longer claims the change has no spec files and now aligns its scope statement with the added spec.
- Re-checked task dependencies: every non-first task has `dependsOn`, all references point to earlier tasks, and no cycles were introduced.

<promise>PASS</promise>
