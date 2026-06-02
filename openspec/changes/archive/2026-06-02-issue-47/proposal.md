## Why

The dedicated session transcript page is supposed to be a faithful read-only projection of the real opencode / coder agent conversation, but `GET /api/issues/{number}/workflow/sessions/{sessionName}` still fabricates the user prompt from `session.Title` (e.g. a short task title like `Cover backend projection and progress behavior`) even though the runner records the full real prompt in the `mohist_prompt` workflow session event. On `/issues/8/workflow/sessions/T-005.1` this is a ~7.5k-character gap between what the agent actually saw and what the page shows. Earlier issues (#158, #184, #190, #205, #220) already moved the rest of the transcript to a real conversation shape; this issue closes the remaining gap so the page is trustworthy as a read-only view of what the coder agent actually saw and did.

## What Changes

- Rebuild the session transcript from the canonical `workflow_agent_session_events` stream. The first `mohist_prompt` event opens the first turn, and each subsequent `mohist_prompt` event opens a new turn in the same transcript, preserving the order of resumed / follow-up prompt rounds.
- Populate `turn.user` from the `mohist_prompt` payload (`text`, `role`, `kind`, `sentAt`, `title`, `outputPath`, `contextFiles`) instead of from `session.Title`. `text` SHALL be the full real prompt as recorded in the event.
- Split the assistant parts list by `mohist_prompt` so that reasoning, text, tool, error, divider, and terminal parts land inside the turn whose prompt preceded them in event order. Reasoning and text parts SHALL keep their natural interleaving across tool boundaries (no more collapsed `giant` reasoning or text parts).
- Project historical liveness / terminal / recovery events (`agent_liveness_status`, `agent_session_terminal`, `coder_recovery_*`) into the assistant parts of the turn they belong to, as divider or error parts, so refreshed pages match live pages.
- For historical sessions that have no `mohist_prompt` event, render an explicit `legacy-missing` prompt state (`kind: 'legacy-missing'`, summary text) and do NOT substitute `session.Title` as the user prompt.
- Ensure the rendered prompt block in the web UI expands to and copies the full real prompt text (not the short task title). Mohist task title, output path, and context files remain visible as context / summary / header on the same prompt block, not as the prompt body.
- Ensure tool parts expose raw `input`, `output`, `metadata`, and `details` through the existing expandable disclosure so tool payload fidelity is not lost when the visible card is summarized.
- Do not change the actual prompt that is sent to opencode, do not introduce a new transcript snapshot table, and do not touch ACP concurrent-session routing.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `agent-session-ui` — the dedicated session page must render the real `mohist_prompt` text as the user prompt (expandable, copyable), must show one turn per `mohist_prompt` in event order, must keep reasoning / text / tool parts interleaved in event order, must surface historical liveness and terminal events, and must show an explicit `legacy-missing` prompt for sessions that have no recorded `mohist_prompt`.
- `coder-session-tracking` — the canonical session event stream is the single source of truth for transcript turn reconstruction. The persisted `mohist_prompt` events must expose every field the transcript needs (`text`, `role`, `kind`, `sentAt`, `title`, `outputPath`, `contextFiles`), and the session-detail projection must use those events to build turns, prompt metadata, and historical terminal / liveness / recovery visibility.

## Impact

- **Backend transcript projection** — `packages/server/src/Mohist.Server/Sessions/Queries/WorkflowAgentSessionQueryService.cs` (`BuildTranscriptAsync`, `BuildAssistantParts`) must be rewritten to:
  - Iterate events in `Sequence` order.
  - Open a new turn on each `mohist_prompt` event and copy the full payload into `turn.user` (including `text`, `kind`, `sentAt`, `title`, `outputPath`, `contextFiles`).
  - Attribute reasoning, text, tool, error, divider, and terminal parts to the currently open turn.
  - Fall back to a single `legacy-missing` turn when the session has zero `mohist_prompt` events.
  - Preserve reasoning / text / tool interleaving by closing the active opposite stream part when a new chunk type arrives.
  - Project `agent_liveness_status` and terminal / recovery events as divider or error parts inside the last open turn.
- **Backend API** — `WorkflowAgentSessionTranscript` (and its serialized JSON shape) must keep `turns[].user.text` equal to the full `mohist_prompt` payload text and `turns[].user.summary` populated with `title` / `outputPath` / `contextFiles`. The metadata block must include `turnCount` equal to the actual number of `mohist_prompt` events.
- **Frontend rendering** — `packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.tsx`, `PromptBlock.tsx`, `TurnList.tsx`, and `model/session-transcript-display.ts` already support `legacy-missing` and full-prompt expansion; they need to be aligned so the full `mohist_prompt` text is what expands, copies, and is visible. Tool cards already expose `rawInput` / `rawOutput` / `metadata` / `details` through disclosure; ensure the rebuilt transcript continues to feed those fields.
- **Frontend tests** — `packages/web/tests/SessionPage.transcript.test.tsx`, `SessionPage.test.tsx`, and `session-transcript-display.test.ts` must cover full prompt expansion, full prompt copy, `legacy-missing` rendering, multiple `mohist_prompt` turns in event order, and raw tool payload visibility.
- **Backend tests** — `packages/server/tests/Mohist.Server.Tests/Specs/WorkflowSessionSpecs.cs` and `AgentSessionSpecs.cs` must cover: a session with a single `mohist_prompt` returns `turns[0].user.text` equal to the full payload text; `T-005.1` (or an equivalent fixture) does NOT return the short task title as `turns[0].user.text`; two `mohist_prompt` events produce two turns in event order; interleaved `thought → tool → thought → text` events appear in the same order inside `turns[*].assistant`; tool update merging keeps raw input / output / metadata / details / status / first-observed position; historical sessions with no `mohist_prompt` produce a `legacy-missing` turn rather than substituting the session title.
- **No workflow-model expansion** — the change is limited to transcript projection and rendering; it does not add a composer, does not change agent control, and does not persist a new transcript snapshot.
