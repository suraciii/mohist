## Why

The session transcript already reconstructs coder tool activity into an opencode-like conversation, but it still drops the semantic meaning that lets users understand what the agent actually did. This change is needed now because `/issue/:number/session/:sessionId` is supposed to answer what Mohist asked, which skill/context/action the coder used, and what changed, yet common tools still collapse into generic labels like `read`, `bash`, `skill`, or `unknown`.

## What Changes

- Preserve semantic tool identity and user-facing titles across tool lifecycle updates so late-arriving `tool_call_update` data can replace generic placeholder labels in both live and replayed transcripts.
- Treat common tool families as first-class transcript concepts: context gathering, file mutation, command execution, planning, delegation, interaction/network, and skill loading.
- Expand context-tool rows and groups with concrete targets such as file paths, directories, glob patterns, grep/search queries, and useful output summaries instead of raw or generic names.
- Render `bash` and `shell` calls with command, working directory, status, exit code when available, and a bounded output preview that helps users understand execution results quickly.
- Render `edit`, `write`, and `apply_patch` as reviewable change content with affected files, operation summaries, and expandable diff or written-content views before raw JSON fallback.
- Stop suppressing user-useful planning and delegation details by rendering todo progress, task/subagent descriptions, child session references, question prompts, URLs, web queries, and loaded skill names semantically.
- Remove prompt metadata duplication so the prompt card shows one readable output target instead of repeating the same output path in multiple lines.
- Keep the existing opencode-like transcript layout, context grouping model, and shared live/persisted display contract intact.

## Capabilities

### New Capabilities

<!-- None. -->

### Modified Capabilities

- `agent-session-ui`
- `coder-session-tracking`

## Impact

- **Backend transcript normalization**: `packages/cli/src/services/session-transcript-service.ts` must refresh display titles and semantic details when tool updates arrive, improve `skill`/`task`/known-family inference, preserve mutation diff metadata, and avoid duplicate prompt output-path summaries.
- **Frontend transcript projection**: `packages/cli/web/src/lib/session-transcript-display.ts` must stop treating todo tools as transcript-only noise when they carry user-meaningful progress and must preserve semantic per-tool detail inside grouped context rendering.
- **Frontend semantic renderers**: `packages/cli/web/src/components/session-transcript/tool-registry.tsx`, shared transcript parsing helpers, and related transcript components must add or complete dedicated renderers for the required tool families and use semantic change/command/detail views as the primary experience.
- **Prompt card rendering**: `packages/cli/web/src/components/session-transcript/PromptBlock.tsx` must collapse duplicate output-target metadata into one readable line.
- **API/display contract parity**: the normalized session transcript payload consumed by `/issue/:number/session/:sessionId` must support the same semantic tool rendering in live SSE updates and persisted replay after refresh.
