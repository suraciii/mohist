## Why

Slack-origin turns already receive a managed collaboration Skill, but its executable contract does not yet fully capture the six documented rules for direct questions, silence, anchored replies, delegation callbacks, and recovery. Issue 617 makes that behavior source-scoped, versioned, and integrity-checked now, without claiming that prompt text can deterministically guarantee model behavior or Slack delivery.

## What Changes

- Update the embedded `mohist-slack-collaboration` Skill to match the six collaboration rules in `docs/slack.md`, including the priority of directly answering a human question over ordinary silence, the prohibition on empty acknowledgements, self-contained conclusions and next steps, and silent continuation after restart, Session recovery, or context compaction.
- Publish the Skill's stable name, version, instructions, and content digest from Server as part of the Slack execution context. Pin the version-to-content mapping so same-version asset drift fails catalog resolution, and require the Runner to validate the context before invoking a Runtime.
- Deliver the same validated Skill and Server-provided reply anchor to both Slack-origin initial execution and Slack-origin follow-up execution.
- Reject an invalid, modified, incomplete, or anchorless Slack execution context before Runtime invocation. Non-Slack execution continues without the Slack Skill or reply anchor.
- Keep reply authorship with the Agent's existing Slack reply action. This change does not add Server-authored missing-reply detection, fallback response generation, or deterministic natural-language question classification.

## Capabilities

- `slack-collaboration-skill`: Defines the canonical six Slack collaboration rules, their direct-question and silent-recovery behavior, their correspondence with the Slack product documentation, and the versioned name/instructions/content-digest integrity contract, including the production-enforced same-version asset lock.
- `slack-skill-injection`: Defines when the versioned Skill and reply anchor are injected into execution, covering Slack initial launches and follow-ups, exclusion from non-Slack execution, and fail-closed validation before Runtime invocation.

## Impact

- **Server:** the embedded Slack collaboration Skill asset and its catalog, the Slack execution-context contracts/factory, and the initial-launch and follow-up dispatch paths that publish the context.
- **Runner:** Slack context parsing and integrity validation, execution-envelope composition, AgentJob execution, follow-up handling, and dispatch validation before Runtime calls.
- **Tests and documentation:** contract tests for the six rules, version/hash integrity, initial/follow-up parity, non-Slack exclusion, and invalid-context rejection; `docs/slack.md` remains the behavioral source of truth.
- **Unaffected systems:** no public API, persistence model, Slack outbox, liveness projection, thread mapping, reply authorization, Web/CLI/Workflow execution, external dependency, or Slack delivery protocol changes. Runtime output remains separate from reply delivery, and the Skill does not guarantee that a model will always answer or that a reply will be delivered.
