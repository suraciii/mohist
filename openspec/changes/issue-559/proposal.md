## Why

`mohist/agent` currently resolves an Agent while translating Workflow work and
rewrites the dispatch to the selected runtime Action. That keeps the Workflow
task as the execution owner and does not create the generic AgentJob,
AgentSession, Input, and Turn lineage used by direct Agent launches.

Pi and OpenCode are runtime adapters below the generic AgentJob boundary. A
Workflow handoff must preserve that boundary without creating a second
scheduler or embedding a runtime-specific contract in Workflow.

## Change

Add the first, Server-only handoff fence for a new Agent-backed Workflow task
attempt: a durable command identity, immutable future invocation linkage,
preflight snapshot or definitive rejection, and a matching acceptance receipt.

This slice deliberately does not materialize an AgentJob, AgentSession, Input,
Turn, or Runner work. It does not alter the current `mohist/agent` translator
path. A later slice owns participant materialization, typed transport, and the
Workflow finalizer that applies completion effects.

## Non-goals

- Changing direct AgentJob launches, inline runtime Actions, or runtime
  adapters.
- Adding Runner transport, dispatch work, or a new queue.
- Replacing the existing Workflow translation path before a finalizer exists.
- Treating any adapter-specific session behavior as the generic Agent contract.
