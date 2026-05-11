Self-review completed against proposal, design, specs, and tasks.

Fixes applied during review:
- Added missing capability specs for `pipeline-model`, `workflow-definition`, `workflow-config`, and `web-ui`
- Updated `tasks.json` so tasks reference concrete spec files and all specs are covered by implementation tasks
- Split workflow work into separate definition and validation tasks so lifecycle-vs-runnable-stage behavior is independently testable

Checks:
- Alignment: proposal, design, specs, and tasks all describe the same backlog-first pipeline cleanup
- Completeness: specs now cover the affected capabilities, and tasks cover each spec
- Consistency: naming is aligned across artifacts; task dependencies are acyclic and strictly point to earlier priorities
- Feasibility: task outputs build incrementally from shared model to workflow semantics, UI, and regression coverage

<promise>PASS</promise>
