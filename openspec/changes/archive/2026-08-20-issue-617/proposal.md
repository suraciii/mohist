## Why

Slack-origin turns already receive a managed collaboration Skill, but its executable contract does not yet fully capture the six documented rules for direct questions, silence, anchored replies, delegation callbacks, and recovery. Issue 617 makes that behavior source-scoped, versioned, and integrity-checked now, without claiming that prompt text can deterministically guarantee model behavior or Slack delivery.

## What Changes

- Update the embedded `mohist-slack-collaboration` Skill to match the six collaboration rules in `docs/slack.md`, including the priority of directly answering a human question over ordinary silence, the prohibition on empty acknowledgements, self-contained conclusions and next steps, and silent continuation after restart, Session recovery, or context compaction.
- Publish the Skill's stable name, version, instructions, and content digest from Server as part of the Slack execution context. Pin the version-to-content mapping so same-version asset drift fails catalog resolution, and require the Runner to validate the context before invoking a Runtime.
- Deliver the same validated Skill and Server-provided reply anchor to both Slack-origin initial execution and Slack-origin follow-up execution, with an explicit durable `executionSource` discriminator coupling Slack dispatches to their required context.
- Reject an invalid, modified, incomplete, or anchorless Slack execution context before local follow-up enqueue or Runtime invocation; a Slack source with an omitted or null context can never fall through to ordinary work. Non-Slack execution carries an explicit non-Slack source and continues without the Slack Skill or reply anchor.
- Keep reply authorship with the Agent's existing Slack reply action. This change does not add Server-authored missing-reply detection, fallback response generation, or deterministic natural-language question classification.

## Capabilities

- `slack-collaboration-skill`: Defines the canonical six Slack collaboration rules, their direct-question and silent-recovery behavior, their correspondence with the Slack product documentation, and the versioned name/instructions/content-digest integrity contract, including the production-enforced same-version asset lock.
- `slack-skill-injection`: Defines when the versioned Skill and reply anchor are injected into execution, covering Slack initial launches and follow-ups, exclusion from non-Slack execution, and fail-closed validation before Runtime invocation.

## Impact

- **Server:** the embedded Slack collaboration Skill asset and its locked catalog, the Slack execution-context contracts/factory, the durable execution-source and bound-thread-root provenance fields, and the initial-launch and follow-up dispatch paths that publish the context.
- **Runner:** Slack source/context pair parsing and integrity validation, execution-envelope composition, AgentJob execution, follow-up handling, and dispatch validation before local enqueue or Runtime calls.
- **Tests and documentation:** contract tests for the six rules, version/hash integrity, initial/follow-up parity, non-Slack exclusion, and invalid-context rejection; `docs/slack.md` remains the behavioral source of truth.
- **Unaffected systems:** no relational schema migration, Agent definition persistence, Slack outbox, liveness projection, reply authorization, Web/CLI/Workflow behavior, external dependency, or Slack delivery protocol changes. The append-only execution-source/root provenance needed to enforce this wire contract does not change Session queue/turn semantics or thread routing. Runtime output remains separate from reply delivery, and the Skill does not guarantee that a model will always answer or that a reply will be delivered.
