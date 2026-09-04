## Why

Today a user cannot answer "what did each task do, how did it end, and what did it cost" from an Agent's history. The Agent detail page lists sessions as indistinguishable rows (agent name, model, timestamp), repeats the same session across the Running/Failed/Ended/Recent groups, and labels `unknown` activity as "Failed" — contradicting the glossary's certainty vocabulary. The CLI job list shows only lifecycle timestamps with no task, result, context, model, or cost, and no path into the conversation. The facts already exist — Server-owned SessionInputs, AgentTurn results, Job terminal results, usage, and launch context refs — so this is a read-projection and presentation gap, not a data gap. The Session timeline (#427) already explains the execution process; history records, the Session page, and exported results must now share the same execution context so one execution reads consistently everywhere.

## What Changes

- Add a canonical **Agent execution history** read: one distinguishable record per Job or Turn, carrying the task summary (from its SessionInputs), outcome and result summary (including failure reason), launch context (Issue / Epic / repository / workspace), status, start and end time, duration, model, and cost with honest attribution (session-level usage is labeled as such, not fabricated per turn).
- The history is a **read-only projection of authoritative lifecycle facts**: it never re-arbitrates Job, Turn, or Session state; `unknown` renders as unknown, never as failed; Job result stays distinct from Session activity.
- Replace the Agent detail page's session-row list with these history records: no record appears twice, each record is distinguishable by task, result, and context, and each links into its Session page anchored to the corresponding Turn so a refresh keeps the same understanding.
- Expose the same history contract in the CLI so `mo` reads return task, result, context, timing, model, and cost — not bare lifecycle timestamps — and can navigate to the Session.
- Keep the Session page's semantic timeline as the process explanation and align it with the history contract: same context refs, same Job/Turn/result vocabulary, same failure interpretation. This completes and verifies the delivered timeline (#427) rather than rebuilding it.
- Add a **Session result export** that carries the same execution context (Session/Turn/Job identity plus context refs) so an exported result is understandable standalone and matches what the history record and the Session page say.
- No change to Input/Turn lifecycle, recovery, stop, or settlement semantics; no new transcript facts; the existing jobs and sessions list endpoints remain for their current consumers.

## Capabilities

- `agent-execution-history`: The canonical per-Job/per-Turn history record contract — record identity and granularity, fields (task, outcome, result summary, context, status, start/end/duration, model, cost attribution), deduplication, honest unknown/uncertain presentation, and its surfaces in the Server read API, Web Agent detail page, and CLI.
- `session-timeline-interpretation`: The Session page timeline's required interpretation of an execution — distinguishing user input, agent reply, key actions, errors, Compact/Reset boundaries, and unknown states; collapsing low-value noise without hiding failures or domain actions; keeping raw events as an explicit diagnostic view; and presenting results and context consistently with history records, including after refresh.
- `session-result-export`: Exporting a Session's interpreted result with its execution context (stable Session/Turn/Job identity, context refs, public result facts) so history records, the Session page, and the export describe the same execution.

## Impact

- **Server read surfaces:** new history projection over existing session/job facts (`packages/server/src/Mohist.Server/Sessions` — read models, `AgentSessionQuerier`, projection service) and route exposure alongside `Api/AgentSessionListRoutes.cs` / `Api/AgentJobReadRoutes.cs`. No grain or lifecycle changes.
- **Web:** `packages/web/src/pages/agent-detail` (history section replaces session rows; fixes the `unknown`→"Failed" grouping), `entities/agent/api` (history types and query), `pages/session` (context continuity, refresh anchoring, export), `widgets/session-transcript` (alignment to shared context/result vocabulary only).
- **CLI:** `mo agent job` / `mo session` read commands, table and `--json` field contracts for history records and export.
- **Docs:** `docs/web-ui.md` (Agent detail history, implementation gaps), `docs/agent-sessions.md` (history projection), `design/session-timeline.md` (status update), `docs/cli-reference.md`.
- **Dependencies:** none; builds on existing usage, context-ref, and turn-result facts. Coordinates with #589 (settlement) only in vocabulary — blocked/outcome states must not be reinterpreted here.
