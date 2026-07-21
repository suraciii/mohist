# Self Review

## Findings

No blocking plan findings.

The plan now declares `issue-fields` and `workflow-checkpoint` as opaque capabilities for built-ins that require private dispatch context. It keeps parent-Issue prompt composition and OpenCode session metadata inside `agent-turn`, so Actions do not regain broad identity or runtime access. The deferred `mohist/openspec-tasks.task` manifest input replaces name-gated `rawTask` injection while preserving nested templates for generated-task dispatch. The implementation task includes acceptance coverage for each replacement and the original result-effect and promise-projection behavior.

<promise>PASS</promise>
