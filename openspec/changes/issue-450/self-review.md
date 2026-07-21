# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against `docs/actions/pi.md`, `design/runtimes/pi.md`, Action/Session architecture, current Runner/Server/Web seams, and repository testing rules. This review modifies no other file.

## Findings

No blocking findings. The plan is ready to build.

## Coverage

- The direct Workflow `mohist/pi` path covers task and check dispatch, explicit/fail-closed input validation, final-text completion, promise projection, recovery errors, and cleanup reuse without introducing AgentJob or Session-command scope.
- The pinned in-process SDK gate covers Node compatibility, real-surface smoke verification, project-untrusted resources, literal prompts, unattended tools, one completion authority, exact deadline behavior, provider policy, credential confinement, and deterministic fake-backed tests.
- Runtime-aware Workflow Session open/attach preserves logical identity, immutable work directory, guarded runtime changes, lineage, same-name reuse, restart restore, missing-file failure, and complete absolute Pi paths through the required EF migration.
- One process-local coordinator serializes complete task/check turns across OpenCode and Pi for the same logical Session while leaving different Sessions concurrent and avoiding durable/distributed lock scope.
- Existing AgentSession reporting gains observable entry-for-entry acceptance, terminal reporting for success and failure, stale-binding rejection, compaction/provider-retry audit facts, distinct cache-write and cost usage, and explicit runtime-error precedence without adding an outbox or replay protocol.
- Existing Web Session components and milestone classification receive Pi coverage without a Pi-specific view, provider form, model selector, or command controls.

## Structural Checks

- `tasks.json` parses; all six task IDs and dependencies resolve and the graph is acyclic.
- Every referenced spec file and requirement anchor resolves.
- The issue's seven acceptance criteria are assigned to implementation tasks with deterministic verification.
- AgentJob Pi execution, Pi Session commands, runtime-aware catalog/UI, ACP/RPC, process isolation, and a generic `AgentRuntime` remain explicit non-goals.

## Verdict

The plan is coherent, scoped, testable, and implementation-ready.

<promise>PASS</promise>
