## Why

When Mohist Agent work reaches a result — completed, failed, or cancelled — the Web cannot currently explain what that execution produced. The Agent page history lists sessions by Activity only (a dot, the redundant Agent name, model, and a timestamp); sessions with `unknown` Activity are grouped under "Failed", which the product language explicitly forbids (Activity is not the result of one unit of work), and AgentJob has no result view separate from its continuing AgentSession (documented gap). On the Session page, a completed Turn's result renders as a quiet one-line status row with no structured evidence, and the first viewport does not show the most recent result that the spec requires. After any execution, the user must re-read the whole transcript to learn what happened and whether it succeeded.

## What Changes

- The Agent page history becomes result-bearing:
  - Each session row identifies the task (first-input excerpt), its origin and context references (Issue, Epic, repository, workspace, Slack), timestamps, and the current Activity as its own separate signal.
  - Each row shows the outcome of its executions with result vocabulary, not Activity vocabulary: the first AgentJob result (the first AgentTurn supplies it) and the latest Turn outcome — completed with its result message, failed with failure category/reason, cancelled, or unresolved. `unknown` Activity is never labeled "Failed", and a failed Job is never presented as a failed Session.
- The Agent-scoped session list read model carries the facts the history needs: a first-input subject excerpt and the terminal Turn result facts per session. The existing AgentJob read surface and launch observation are reused rather than duplicated.
- The Session view explains results in its first viewport:
  - The header surfaces the most recent result — the latest Turn's outcome and its result or failure evidence — distinct from the Activity badge.
  - For launch-origin sessions, the first AgentJob result is presented as the launch result and stays distinct from later Turn results; later Turns never rewrite it.
- The Session timeline presents terminal Turn results as first-class outcome entries:
  - Sentence-form summaries for completed, failed, cancelled, and unresolved Turn outcomes instead of a muted status line for completed results; failed outcomes remain prominent error entries that never collapse.
  - Expandable structured result evidence: result message, output excerpt, failure category and reason, exit code, and the inputs the Turn processed — layered on the same facts as the existing raw view.
- Documentation converges: the docs/web-ui.md Agents gap "AgentJob has no result view separate from its continuing AgentSession" is closed, the AgentSession implementation-gap footnote is updated so the result-presentation gaps this change removes no longer appear, and design/session-timeline.md defines result-entry semantics.

## Capabilities

- `agent-history-results`: The Agent page history explains each execution — per-session task subject, origin and context references, current Activity, and execution outcomes (first AgentJob result and latest Turn result) presented with result vocabulary including honest unknown handling, and the session-list read-model facts it consumes.
- `session-result-presentation`: The Session view explains execution results — the most-recent result in the first viewport, the first-launch AgentJob result distinct from later Turn results, and terminal Turn outcome entries in the timeline with structured, expandable result evidence.

## Impact

- **Web (`packages/web`)**:
  - `pages/agent-detail` — history section rewrite: result-bearing rows, outcome-based grouping, Activity/result separation.
  - `entities/agent` — session list DTO fields and queries; AgentJob read queries if used for launch results.
  - `pages/session` — first-viewport result summary in the session header.
  - `entities/session/model/timeline` and `widgets/session-transcript` — Turn result outcome items with structured detail.
- **Server (`packages/server`)**:
  - `Sessions/Services/AgentSessionQuerier` and its DTOs — agent-scoped and unified session lists gain subject excerpt and latest-Turn result facts; read-only, no state, event, or transcript-fact changes.
- **Docs**: `docs/web-ui.md` (Agents and AgentSession sections and gap footnotes), `design/session-timeline.md`.
- **Tests**: Web entity/widget/page specs for history rows and result presentation; server read-model tests.
- **Dependencies**: none. No transcript fact, event protocol, or state-authority changes — this is a read and presentation layer over already-recorded facts.
