## Context

`acp-session.ts` accumulates all `agent_message_chunk` text into `agentText` (returned as `result.text`), which includes LLM reasoning, tool-use narration, and debugging output. `workflow-controller.ts` stores this raw accumulation directly as `selfReviewNotes` (plan stage, line 235) and `reviewReport` (review stage, line 689).

The review prompt (`review.md`) already instructs the agent to write a structured `review.md` file to `{changeDir}/review.md`, but the workflow controller never reads it back. The self-review prompt does NOT yet instruct writing to disk — this needs to be added.

Additionally, `agent_thought_chunk` events (extended thinking/reasoning) are completely unhandled in `acp-session.ts`, meaning they're neither logged nor filtered.

## Goals / Non-Goals

**Goals:**
- Store clean, structured reports for `selfReviewNotes` and `reviewReport` instead of raw LLM session text
- Exclude `agent_thought_chunk` content from `agentText` accumulation
- Maintain backward compatibility via fallback to `result.text` when output files are absent

**Non-Goals:**
- Parsing or validating the structure of report files (agent owns format)
- Changing how `agentText` is used for build/coder stages (only plan self-review and review stages need file-based extraction)
- Modifying the EventBus push behavior or `coder_text_chunk` events

## Decisions

### D1: Read report from disk file after stage completion

After plan self-review and review stages complete, read the output file from `{changeDir}/` instead of using `result.text`. Implement a helper function `readReportFile(changeDir, filename)` that returns file content or `null`.

**Why:** The agent already writes structured reports to disk (review prompt) or can be instructed to (self-review). Disk files represent the agent's final clean output, not its thinking process.

**Alternatives considered:**
- Post-process `result.text` to extract report section — fragile, format-dependent
- Add a special ACP event type for "final output" — requires opencode protocol changes

### D2: Add file-write instruction to self-review prompt

Add an instruction to `self-review.md` telling the agent to write its review summary to `{changeDir}/self-review.md`, mirroring what the review prompt already does for `review.md`.

**Why:** Without this, the plan stage has no disk file to read from, and the fallback to `result.text` would always trigger.

### D3: Skip `agent_thought_chunk` in agentText accumulation

In `acp-session.ts:565`, the `if` block only handles `agent_message_chunk`. Add an explicit early-return or no-op branch for `agent_thought_chunk` so it's never appended to `agentText`. The event is still logged to `workflow_log` (that code runs unconditionally after line 597).

**Why:** Minimal change — `agent_thought_chunk` already falls through to the `else` branch (line 593) which calls `onSessionUpdate` but doesn't touch `agentText`. However, being explicit prevents future regressions if the branching logic changes.

**Alternatives considered:**
- Filter at the `result.text` level after session ends — loses the distinction between message and thought content
- Strip thought content via regex post-hoc — unreliable

## Risks / Trade-offs

- [Agent doesn't write the file] → Fallback to `result.text` ensures no data loss, same behavior as today
- [Self-review prompt change alters agent behavior] → Minimal risk; adding a file-write instruction doesn't change review logic

## Migration Plan

No migration needed. Both changes are backward-compatible:
1. Existing issues in review gate will show raw `result.text` (unchanged)
2. New runs after deploy will produce clean reports from disk files
3. `agent_thought_chunk` filtering is transparent — `agentText` was already supposed to be message-only

## Open Questions

None.
