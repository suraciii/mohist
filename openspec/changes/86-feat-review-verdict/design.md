## Context

The approval panel in `IssueDetailPage.tsx` currently renders identically for all gates: a `max-h-64 overflow-y-auto` div with `whitespace-pre-wrap` text, and a single "Approve & Continue" button. The backend already parses verdict (`parseVerdict` via `## Result: PASS/FAIL` regex) but discards it — the `output` object only contains `{ stage, issueNumber, reviewReport }` or `{ stage, issueNumber, selfReviewNotes }`.

The review prompt (`review.md`) already produces structured markdown with per-dimension `### DimensionName: PASS/FAIL` sections. The plan stage produces artifacts at known paths (`proposal.md`, `design.md`, `specs/`, `tasks.json`) but these are never exposed to the UI.

The existing `POST /api/issues/:number/messages` endpoint already injects text into a paused agent session and resumes it — this is the mechanism for "send back for fixes" without needing a new API.

## Goals / Non-Goals

**Goals:**
- Backend stores parsed verdict + dimension breakdown in `approvalState.output`
- Frontend renders structured review summary (verdict badge → dimension grid → expandable markdown report)
- Frontend renders plan-stage artifact previews
- Frontend provides verdict-aware action buttons (approve / send-back / force-approve)
- All reject flows use existing `POST /api/issues/:number/messages` endpoint

**Non-Goals:**
- New backend API endpoints (messages endpoint already handles reject)
- Changing the review prompt format (it already produces `## Result: PASS/FAIL` and `### Dimension: PASS/FAIL`)
- Server-side artifact content API for plan stage (frontend reads from `approvalState.output.artifacts`)
- Changing the auto-fix loop behavior (Issue #65/#79 scope)

## Decisions

### D1: Parse dimensions from review.md markdown at backend storage time

The review prompt already produces `### Correctness: PASS / FAIL` sections. Rather than parsing on the frontend, add a `parseDimensions()` function in `workflow-controller.ts` that extracts dimension names and pass/fail status from the review report markdown, using regex `###\s+(\w[\w\s]*?):\s*(PASS|FAIL)`.

Store the parsed result in `approvalState.output` alongside the raw `reviewReport`.

**Alternatives considered:**
- Parse on frontend → requires duplicating parsing logic, frontend gets inconsistent data if format drifts
- Change review prompt to output JSON → breaks existing auto-fix/re-verify prompts that consume the markdown

### D2: Store plan-stage artifact summaries in output at storage time

For plan stage, read `proposal.md`, `design.md`, and `tasks.json` from the change directory when storing the output. Store as `artifacts` array with `{ name, path, content }` entries. Cap each artifact's content at 5000 chars to avoid bloating the DB field.

**Alternatives considered:**
- New API endpoint to read artifacts from filesystem → adds API surface, changeDir path is ephemeral
- Lazy-load via SSE → over-engineering for content that's static once plan completes

### D3: Reuse existing messages endpoint for all reject flows

"Send back for fixes", "Send back with instructions", and "Send back with notes" all use `POST /api/issues/:number/messages` with formatted message content. No new API needed.

The message format differs by flow:
- Fixes: prefix + review report
- Instructions: user message + review report reference
- Notes: user message with plan feedback prefix

**Alternatives considered:**
- New reject endpoint → messages endpoint already does this job, adding another would be redundant
- Reject + auto-resume flag → messages endpoint already resumes the agent

### D4: Extract approval panel into separate components

Extract the monolithic approval section from `IssueDetailPage.tsx` into:
- `ApprovalPanel` — top-level wrapper that switches on stage
- `PlanApprovalPanel` — plan artifact preview + actions
- `ReviewApprovalPanel` — review summary + verdict-aware actions
- `ReviewSummary` — verdict badge + dimension grid + expandable report (reusable)

This keeps `IssueDetailPage.tsx` manageable (currently 953 lines).

**Alternatives considered:**
- Keep everything inline in IssueDetailPage → already too large, adding more conditional rendering makes it unreadable

### D5: Use lightweight markdown renderer for report display

Add `react-markdown` (or similar lightweight library) for rendering the expandable full report section. Check `package.json` first — if already available, use it; otherwise use a minimal approach.

**Alternatives considered:**
- Custom markdown-to-HTML → reinventing the wheel
- Raw `dangerouslySetInnerHTML` → XSS risk
- Keep `whitespace-pre-wrap` → violates spec requirement for markdown rendering

## Risks / Trade-offs

- [Artifact content size] → Cap at 5000 chars per artifact; if truncated, show "truncated" indicator
- [Dimension parsing regex fragility] → Regex matches the exact format the review prompt produces; if prompt changes, regex must update. Mitigated by keeping parser next to prompt in same package
- [No artifact content API means content is fixed at plan completion time] → If artifacts change after plan (edge case), displayed content may be stale. Acceptable since artifacts don't change after plan stage completes
- [Force Approve UX] → 3-second confirmation timeout is short enough to prevent accidental clicks but long enough to read. Mitigated by visual style change on first click

## Migration Plan

1. **Backend first**: Add `parseDimensions()` + `parseVerdict()` enrichment to `workflow-controller.ts` output objects. Deploy — existing frontend ignores new fields (backward compatible).
2. **Frontend types**: Update `ApprovalOutput` type in `types.ts`. Deploy — no visual change yet.
3. **Frontend components**: Add new approval panel components, replace old inline rendering. Deploy — new UI active.
4. **No rollback concern**: Old frontend simply ignores `verdict`/`dimensions`/`artifacts` fields in output. Rolling back frontend alone is safe.

## Open Questions

- ~~Does `react-markdown` already exist in the project's dependencies?~~ **Resolved**: Yes, `react-markdown ^10.1.0` is in `packages/cli/web/package.json`. No new dependency needed.
