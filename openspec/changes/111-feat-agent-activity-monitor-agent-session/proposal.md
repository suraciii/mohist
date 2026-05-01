## Why

Users running 3-8 AI agents in parallel on different issues have no global view of agent session status — they must click into each issue individually to check if agents are running, stuck, or failed. This creates a "blind spot" where stalled sessions go unnoticed until the user happens to check.

## What Changes

- Add `GET /api/agent/sessions` endpoint returning a cross-issue list of all coder sessions with issue info, session status, model, timestamps, and last-activity time (derived from workflow_log)
- Add `/activity` page with StatusBar (active/waiting/completed/failed counts + slot usage), Active/Waiting/Recent session card groups, and anomaly detection (running >30min, idle >5min, stuck recovery, unanswered questions >10min)
- Session cards show issue number, title, task description, stage label, model, running duration (live-updated), last 3 activity previews, and task progress bar (from ralph_task_update)
- Click-through from cards to `/issue/:number/session/:sessionId` for full conversation view
- Real-time updates via existing SSE events (coder_session_started/completed, coder_text_chunk/tool_call, ralph_task_update/loop_progress, agent_paused, question_asked)
- Add Activity navigation entry in Header and MobileBottomNav

## Capabilities

### New Capabilities

- `agent-activity-page`: The `/activity` page — StatusBar, session card groups (Active/Waiting/Recent), anomaly detection badges, real-time SSE-driven updates, click-through navigation

### Modified Capabilities

- `http-api`: New `GET /api/agent/sessions` endpoint with `?status=` and `?limit=` query params
- `web-ui`: Activity entry in Header and MobileBottomNav navigation
- `agent-session-ui`: SSE event consumption extended to support the activity page's card-level updates (activity previews, progress bars)

## Impact

- **Backend**: New route handler in `packages/cli/src/api/`, new query method joining `coder_session` + `issues` + `workflow_log` tables
- **Frontend**: New `ActivityPage` component in the WebUI, new route `/activity`, SSE subscription hooks extended for card-level event handling
- **Database**: No schema changes — reuses existing `coder_session`, `issues`, `workflow_log`, `questions` tables
- **Dependencies**: No new external dependencies
