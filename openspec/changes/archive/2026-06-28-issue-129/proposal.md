## Why

Mohist can already run external agents, but only inside a workflow tied to an issue — every agent session is reached through `workflowRunId + sessionName`. The two prerequisites that should unlock ad-hoc agent use are built yet disconnected: the `Agent` entity (#128) holds reusable instructions/config/skills but is never consumed by any launch path, and the standalone `AgentJob` engine (#126) takes a raw prompt, ignores `Agent` definitions, and produces no `AgentSession` (it dispatches with `workflowRunId = ""`, so the runner skips session recording). As a result a user cannot pick an agent profile and just talk to it the way they would in Codex, OpenCode, or Hermes. This change wires Agent profile → AgentJob → AgentSession into a product-level generic session entry point: the core of the Agent workbench.

## What Changes

### A. Launch a generic AgentSession from an Agent profile

- New product API to start a generic `AgentSession` from a project-scoped `Agent`: the launch resolves the Agent definition and combines its `Instructions` + `AgentConfig` + the user's prompt (+ optional context references) into the execution input.
- `AgentJob` execution consumes an `Agent` definition rather than only a raw `Prompt`; an `AgentId`/agent source is carried on the job input.
- The job records an `AgentSession` (transcript + run events) so the launch is observable the same way workflow sessions are. The `AgentSession` domain model already supports life without a `WorkflowRun` (workflowRunId is optional labels), so no schema change is required — only new labels/lookup keys (e.g. `source-kind = "agent-launch"`, agent id/name, context refs, workspace/repository).
- Optional context references (issue, epic, project, repository, workspace path) are session metadata / prompt context only — they do **not** create scope, mount, or supervisor lifecycle.

### B. Continue a generic AgentSession

- New product API to send a follow-up to an existing generic `AgentSession`, reusing the same delivery mechanism as workflow follow-ups.
- The runner delivers follow-ups to a generic session the same way (ACP `prompt()` fire-and-forget, runLoop iteration-boundary pickup, `followup` PromptKind tagging).

### C. Runner supports generic (non-workflow) session targets

- **BREAKING (internal runner↔server session contract):** the runner identifies a live session exclusively via the `(projectId, workflowRunId, sessionName)` triplet — `AcpSessionManager.key`, `sessionTargetFromContext`, the runner→server session routes, and `resolveFollowupTarget` all hardcode `workflowRunId`. This generalizes so a generic session (no `workflowRunId`) can be opened, attached, and have runtime events streamed back. Workflow-shaped sessions continue to work; the contract now also admits a generic session target.
- Generic session open / attach / runtime-events 回流 no longer require a `workflowRunId`/`sessionName` pair.

### D. Minimal cancel / terminate semantics

- New product API for cancel/terminate. If the underlying agent cannot be cancelled, or the session is already terminal, the API returns that state explicitly rather than pretending success.

### E. CLI entry

- New CLI command(s) to launch a session from an agent profile, send a follow-up, cancel a running session, and return the session id + status.

## Capabilities

### New Capabilities

- `agent-session-launch`: Product-level generic AgentSession lifecycle — launching a session from a project-scoped Agent profile (combining instructions + agent config + user prompt + optional context references), executing via standalone AgentJob with session/transcript recording, generic session identity & metadata (agent id/name, source, context refs, workspace/repository), runner-side generic session open/attach/runtime-events (session targets not tied to `workflowRunId`), and minimal cancel/terminate semantics.

### Modified Capabilities

- `session-followup`: Follow-up delivery target identification generalizes beyond the `(workflowRunId, sessionName)` pair so a generic (non-workflow) AgentSession can receive follow-ups via the same SignalR → runner → ACP `prompt()` mechanism. Existing workflow-session follow-up behavior is preserved.
- `http-api`: Adds product-level endpoints for launching a generic AgentSession from an Agent profile, sending a follow-up to a generic session, and cancel/terminate. Distinct from the existing issue-scoped follow-up route, which remains unchanged.
- `cli-interface`: Adds CLI entry points to launch a generic session from an agent profile, send a follow-up, cancel a running session, and report session id + status.

## Impact

- **Server / Agent**: `AgentJobGrain` / `AgentJobInput` (`packages/server/src/Mohist.Server/Agent/Grains/`) gain an Agent definition source so instructions/config are consumed rather than only a raw prompt.
- **Server / Sessions**: reuse `AgentSession` + `AgentSessionGrain` unchanged; add generic-session lookup labels/keys alongside `WorkflowAgentSessionMetadata` / `AgentSessionQueryMetadataKeys` (`packages/server/src/Mohist.Server/Workflow/Services/Sessions/`). Generalize `AgentSessionQuerier.ResolveFollowupTargetAsync` which today returns null when `workflowRunId` is blank.
- **Server / API**: new endpoints parallel to `IssueRoutes.Sessions` follow-up and distinct from the validation-only `AgentJobController`; follow-up hub payload (`RunnerHub` `ReceiveFollowup`) generalizes its target.
- **Runner**: generalize `AcpSessionManager.key` (`packages/runner/src/runtime/acp-connection.ts`), `sessionTargetFromContext` (`packages/runner/src/actions/acp/session-events.ts`), the workflow-session strategy (`packages/runner/src/actions/acp/session-strategies.ts`), runner→server session methods (`packages/runner/src/server/connection.ts`), `resolveFollowupTarget` (`packages/runner/src/runtime/host.ts`), and `handleFollowup` (`packages/runner/src/server/runner-signalr.ts`) off the `workflowRunId` axis.
- **CLI**: new command(s) under the agent/session group in `packages/cli/Mohist.Cli/`.
- **Dependencies**: builds on completed #126 (AgentJob) and #128 (Agent entity). No new external dependencies; no LLM-provider calls from Mohist (runner invokes the installed external agent).
