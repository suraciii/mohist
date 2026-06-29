## Context

The self-driving Epic lifecycle (`idle` / `running` / `paused` / `done` / `closed` with Start / Pause / Resume and auto-advancement) shipped across server, CLI, and Web, and the prerequisite UX (issues 278, 279, 281) has landed a stable target experience. But the user-facing docs still describe the old static model:

- `docs/epics.md:141` presents the legacy `active` / `done` / `closed` three-state lifecycle, the field table (`epics.md:57`) lists `status` as `active / done / closed`, and `epics.md:172` claims "Epic 只是组织工具，不参与执行".
- `docs/concepts.md:82` frames Epic as just "一组相关 Issue 的集合".
- `docs/web-ui.md:133` describes the Epics page only as "列出所有 epic".
- `docs/cli-reference.md:196` carries the stale note "当前 CLI 不支持的：Epic 管理（用 API…）" even though `mo epic start/pause/resume/done/close/link/unlink/list/create/show/update` all exist.
- `README.md` / `getting-started.md` one-liners describe Epic as "把零散 issue 组织成产品里程碑/路线".

Current Web UI copy is already at target (`Start Epic`, `Pause`, `Resume`, `Mark Done`, `Start next issue`, list groups `Running` / `Ready to start` / `Waiting / Blocked` / `Idle / Empty`, `EpicStatus` has no `Active` member and `parseEpicStatus('active')` maps legacy values to `Idle`). So this change is **documentation and copy only** — no runtime, API, persistence, or component-structure change.

Stakeholders: docs readers (the product owner persona), and contributors relying on docs to match behavior. Constraints: match the already-frozen product surface from 278/279/281; do not duplicate or contradict the runtime spec'd by `epic-lifecycle` / `epic-list-presentation` / `epic-detail-summary` / `epic-create-flow`.

## Goals / Non-Goals

**Goals:**
- Make `docs/epics.md` reflect the five-state self-driving lifecycle, Start/Pause/Resume (idempotent, multi-surface), auto-advancement, running-but-idle / `nextIssueReason`, and the corrected Epic↔workflow relationship.
- Update `docs/concepts.md` to frame Epic as the planning + advancement unit (default `idle`, Start to begin).
- Update `docs/web-ui.md` Epics page section to document the list-page state groups and detail-page lifecycle actions, matching `packages/web`.
- Update `docs/cli-reference.md` to drop the stale "unsupported" note and document the real `mo epic` subcommands.
- Spot-check `README.md` and `getting-started.md` one-liners; align any that imply the static model.
- Audit Web UI copy for stragglers implying the legacy model (bare `Active` Epic status, ambiguous bare `Start`, `No linked issues` used in a way that implies active progression) and align any found.

**Non-Goals:**
- No new tutorial/site structure.
- No change to Epic lifecycle runtime behavior, API, persistence, or runner.
- No new Web UI components or restructuring; copy wording only if a straggler is found.
- No documenting internal implementation details (Orleans grains, persistence layout, etc.).

## Decisions

### Decision 1: Doc structure — edit in place, no new files

Rewrite the offending sections inside the existing `docs/*.md` files rather than creating new pages. `docs/epics.md` keeps its identity as "the Epic doc"; we replace its lifecycle table, field table, recommended workflow, and "和 workflow 的关系" section in place.

- *Rationale*: Minimal surface change, preserves inbound links, keeps the docs index stable. The proposal explicitly excludes "新增教程站点结构".
- *Alternatives considered*: (a) Split lifecycle into a dedicated `docs/epic-lifecycle.md` — rejected as premature and contrary to the Non-Goals. (b) Keep a "legacy model" note for migration readers — rejected; the proposal requires the legacy model be removed, not preserved alongside.

### Decision 2: Lifecycle section as a state + action matrix

Replace the `active / done / closed` table with a five-row table (`idle`, `running`, `paused`, `done`, `closed`) each with **含义** and **进入条件**, immediately followed by a Start/Pause/Resume action block (per-surface: CLI / Web UI / API) noting idempotency and the "Start attempts to advance the first startable linked issue" semantics. Then a short "自动推进与 running-but-idle" subsection that states auto-advancement applies only to `running` epics and frames running-but-idle as an observable situation explained by `nextIssueReason`, explicitly **not** a sixth state.

- *Rationale*: Mirrors the structure of the spec requirements (states → actions → advancement) so reviewers can map each doc paragraph to a spec scenario. Keeps `done`/`closed` as terminal, `idle` as default — directly satisfying the "default is idle, not active" scenario.
- *Alternatives considered*: A prose-only narrative without a table — rejected; tables make the "default state" and "terminal" facts scannable, which is what the legacy table got right structurally.

### Decision 3: Single source of truth — `epics.md` owns the lifecycle, others link to it

`concepts.md`, `web-ui.md`, `cli-reference.md`, `README.md`, `getting-started.md` each carry a short, accurate Epic statement and link to `epics.md` for the full lifecycle. `web-ui.md` and `cli-reference.md` additionally enumerate the UI groups / CLI subcommands (their surface-specific value-add), but do not re-state the state machine.

- *Rationale*: Avoids the drift that caused this issue in the first place — one canonical lifecycle description, per-surface pages only describe their own surface. This matches the existing "详见 [用 Epic 规划](epics.md)" convention already used by `concepts.md` and `web-ui.md`.
- *Alternatives considered*: Duplicate the lifecycle into every page — rejected; N copies guarantee N-way drift the next time behavior changes.

### Decision 4: Web UI audit is verification-only, edit-on-find

Because issues 278/279/281 already aligned the Web UI, treat the copy audit as a grep-driven verification pass over `packages/web/src/pages/epics/`, `packages/web/src/pages/epic-detail/`, `packages/web/src/entities/epic/`, `packages/web/src/features/*-epic/`, and `packages/web/src/widgets/epic-*`. Only edit if a straggler is found (e.g. a user-visible bare `Active` Epic label, or an ambiguous bare `Start`). Internal symbol names like `groupActiveEpics` / `ActiveEpicGroups` / `IssueHealth.Active` are **not** user-facing copy and are left as-is (renaming them would be a Non-Goal component restructuring).

- *Rationale*: The spec's copy-alignment requirement targets user-visible wording; internal naming that never reaches the DOM is out of scope and changing it risks churn/tests for zero user benefit.
- *Alternatives considered*: Proactively rename internal `groupActiveEpics` → `groupNonTerminalEpics` — rejected as scope creep and Non-Goal.

### Decision 5: CLI reference — enumerate real subcommands, delete the stale note

Remove the `cli-reference.md:196` bullet "当前 CLI 不支持的：Epic 管理（用 API…）" and add an Epic subsection that lists the actual `mo epic` subcommands (`create`, `show`, `list`, `update`, `link`, `unlink`, `start`, `pause`, `resume`, `done`, `close`), cross-linking to `epics.md` for semantics.

- *Rationale*: The note is factually false today; the subcommands are the CLI surface users need to discover.
- *Alternatives considered*: Auto-generate the CLI section from `mo epic --help` output — rejected for now (out of scope; manual enumeration is sufficient for a docs sync and avoids a build-time dependency).

## Risks / Trade-offs

- **[Drift recurrence]** Docs and runtime diverge again on the next lifecycle change. -> *Mitigation*: Keep one canonical lifecycle in `epics.md` (Decision 3) so future changes touch one place; the proposal already scopes this change to alignment only.
- **[Docs/UI mismatch slips through]** A doc claims a UI path or group label that doesn't match `packages/web`. -> *Mitigation*: During implementation, grep `packages/web` for the exact labels (`Start Epic`, `Pause`, `Resume`, `Mark Done`, `Start next issue`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty`) and copy them verbatim into `web-ui.md`; verify with the existing Web tests as the source of truth.
- **[Over-editing Web copy]** Risk of changing working, tested UI strings and breaking 278/279/281 tests. -> *Mitigation*: Decision 4 — edit-on-find only; run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` after any copy edit.
- **[Stale doc examples]** Code samples (`mo epic start …`, curl endpoints) could be invented rather than verified. -> *Mitigation*: Derive CLI examples from the real `mo epic` command surface and HTTP examples from existing `Api/EpicRoutes.cs` patterns already referenced elsewhere in the docs.

## Migration Plan

This is a docs/copy-only change with no runtime, schema, or API impact, so there is no data migration and no staged rollout.

- **Deploy**: merge the doc edits and any Web copy straggler fixes in one PR; docs render statically, Web copy ships with the next Web build.
- **Verify post-merge**: open `docs/epics.md`, `concepts.md`, `web-ui.md`, `cli-reference.md` and confirm (a) no occurrence of `active` as an Epic status, (b) the five states + Start/Pause/Resume + auto-advancement + running-but-idle are present, (c) the CLI "unsupported" note is gone. For Web, run the packages/web test suite.
- **Rollback**: revert the PR. No runtime state to recover.

## Open Questions

- Should the docs mention the `parseEpicStatus('active') → Idle` legacy-mapping behavior for users who still see `active` in old API responses / exports? *Leaning no* — it is an internal compatibility detail (Non-Goal: "不记录内部实现细节"), and the user-facing model is the five states. Confirm during implementation.
- Does `getting-started.md`'s one-liner "把零散 issue 组织成产品路线" need a stronger self-driving verb, or is a lighter touch enough given it's an intro pointer? Decide at edit time based on surrounding paragraph tone.
