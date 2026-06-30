## Why

The self-driving Epic lifecycle (`idle` / `running` / `paused` / `done` / `closed` with Start / Pause / Resume and auto-advancement) is now the actual product behavior across backend, CLI, and Web UI — but the user-facing docs still describe Epic as a static `active / done / closed` organizer that "does not participate in execution" (`docs/epics.md:141`, `docs/epics.md:172`). A reader of `docs/epics.md` today forms a mental model that directly contradicts what the product does, and `docs/cli-reference.md:196` still claims Epic management is unavailable from the CLI even though `mo epic start/pause/resume/done/close/link/unlink/list/create/show/update` all exist. Now is the right moment because the prerequisite UX work (issues 278, 279, 281) has landed a stable target experience, so the docs can be synced to a fixed surface rather than chasing a moving implementation.

## What Changes

- `docs/epics.md` lifecycle section SHALL drop the legacy `active / done / closed` three-state model and document the five states (`idle`, `running`, `paused`, `done`, `closed`) with their meaning and entry conditions.
- The same doc SHALL document Start / Pause / Resume (CLI + Web UI + API), including idempotency and the fact that Start attempts to advance the first startable linked issue.
- The same doc SHALL document running-but-idle as an observable situation explained by `nextIssueReason`, not a new state, and SHALL document auto-advancement (a `running` epic advances the next startable linked issue when the in-progress one reaches a terminal state; `idle` / `paused` epics do not auto-advance).
- The inaccurate statement "Epic 只是组织工具，不参与执行" SHALL be replaced with the correct framing: an Epic influences advancement of its linked issues, but does not change each issue's own workflow execution rules.
- `docs/concepts.md` Epic section SHALL reflect the self-driving role (default `idle`, Start to begin autonomous progression, Epic as the planning + advancement unit — not just an organizing folder).
- `docs/web-ui.md` Epics page section SHALL describe the list-page state groups (Running / Ready to start / Waiting / Blocked / Idle / Empty) and the detail-page lifecycle actions (Start Epic / Pause / Resume / Mark Done) consistent with the actual UI.
- `docs/cli-reference.md` SHALL remove the "当前 CLI 不支持的：Epic 管理" note and document the real `mo epic` subcommands, including `start`, `pause`, `resume`.
- Spot-check `docs/README.md` and `docs/getting-started.md` for any Epic one-liner that still implies the old static model.
- The Web UI copy from issues 278 / 279 / 281 already matches the target experience; this change SHALL audit it for stragglers (e.g. bare `Active`, bare `Start`, or `No linked issues` used in a way that implies the old model) and align any remaining mismatches. No new runtime behavior is introduced.

## Capabilities

### New Capabilities

- `epic-docs`: The contract for user-facing Epic documentation accuracy — the docs MUST describe the self-driving lifecycle (`idle` / `running` / `paused` / `done` / `closed` + Start / Pause / Resume + auto-advancement + running-but-idle / `nextIssueReason`), MUST NOT describe the legacy `active / done / closed` three-state model or frame Epic as a purely static organizer, and the documented CLI / Web UI / API paths MUST match the actual product surfaces. Includes the doc-vs-UI copy alignment guarantee for terms like `Active`, `Start`, and `No linked issues`.

### Modified Capabilities

None. The runtime behavior is fully spec'd by `epic-lifecycle`, `epic-list-presentation`, `epic-detail-summary`, and `epic-create-flow`; this change aligns docs to those requirements and does not alter them.

## Impact

- **Docs** (`docs/`): `epics.md` (lifecycle section, "和 workflow 的关系" section, field table, recommended workflow), `concepts.md` (Epic paragraph), `web-ui.md` (Epics page section), `cli-reference.md` (Epic subcommand coverage and the stale "not supported" note), spot-check `README.md` and `getting-started.md`.
- **Web UI** (`packages/web`): audit `pages/epics/` and any Epic-related copy for stragglers that imply the old model; align if found. No component restructuring, no new components.
- **Server / API / Runner / Persistence**: No change. Risk driver is documentation and copy only.
- **Dependencies**: None.
