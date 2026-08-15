# Proposal: Authorize Workflow Cleanup Turns

When a Workflow Agent action leaves a dirty worktree, Runner cleanup
reinvokes the same runtime action. The current reinvocation preserves the
normal Workflow `session.input` contract, so the Server treats cleanup as a
second Workflow execution for the original task. Once the original turn has
returned, that task binding is no longer a valid admission for another
Workflow execution. The failure is reported as `AgentSession rejected
session.input`, and the cleanup never gets a chance to commit the proposal.

Cleanup MUST remain on the same physical AgentSession and runtime Session,
but it MUST use a distinct, deterministic cleanup operation and Agent turn.
The Server owns the cleanup admission and authorizes it only against the
original frozen execution binding and a still-running Workflow task attempt.
The Runner outbox MUST order the cleanup admission after the original
terminal runtime facts while preserving the original immutable execution
identity on normal Workflow events.

The implementation is runtime-neutral and covers Pi and other generic Agent
turns. It does not add an OpenCode-only fallback, retry a rejected input, or
silently discard dirty files.
