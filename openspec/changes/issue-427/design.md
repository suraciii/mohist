## Context

The session transcript is rendered by the `session-transcript` widget (`packages/web/src/widgets/session-transcript/`). `SessionDetailShell` (`pages/session/ui/SessionDetailShell.tsx`) hosts `SessionTranscriptLayout`, which today composes a centered two-column grid: a main column (`TurnList` + `TranscriptToolbar`) and a desktop `TurnTocRail` (180px). Inside `TurnList`, each `TurnItem` renders a right-aligned `TurnHeader` ("Turn N · time"), a `PromptBlock`, `AssistantParts`, and a `TurnDiffs` summary.

The current shape is a chat UI, not a timeline:

- `TurnList` caps everything at `max-w-2xl mx-auto`; `SessionTranscriptLayout` wraps it in `lg:max-w-4xl` with the TOC grid.
- `PromptBlock` is a right-aligned `rounded-2xl` bubble at `max-w-[80%]`.
- `AssistantTextPartView` caps assistant markdown at `max-w-[80%]`.
- Each tool call renders as a bordered card (`ToolRowView` / `ContextGroupView` in `ui/tool-views/index.tsx`, plus the older `ToolCallCard`), expanded by default with input/output `<pre>` blocks — the "card wall".

The **data model is already sufficient** and is explicitly out of scope to change (issue Non-Goals). `projectTurn` (`model/session-transcript-display.ts`) already projects `DisplayTurn` → `assistantParts` of kinds `text` / `reasoning` / `tool` / `context-group` / `error` / `divider`, each `DisplayToolPart` carrying `status`, `startedAt`/`completedAt`, `changedFiles` (with `additions`/`deletions`), `rawInput`/`rawOutput`, `normalizedName`, and failure flags. Consecutive exploratory calls are already merged into `context-group` parts. So this is a **pure presentation rewrite**: same projection in, different rendering out.

Constraints / boundaries (epic #49): #426 already landed the minimal transcript-data correctness fixes this builds on. #428 (live-ticking duration, current-activity bar), #429 (navigation sidebar / mini-timeline / error jump-to), and #430 (session header/framework) are separate and will mount on the row structure this issue produces — so each tool row must expose a **stable, locatable, semantic structure**. No data model, event protocol, collection pipeline, or shell-contract change.

See `proposal.md` for motivation and `specs/{session-transcript-timeline,transcript-tool-rows,transcript-context-grouping}/spec.md` for the required behavior.

## Goals / Non-Goals

**Goals:**
- Render the transcript as a flat single-column timeline: one shared left margin, content fills the column, no bubble width-caps on turns / assistant text / prompts.
- Give each turn a full-width divider bar (ordinal, prompt type, start time, turn duration).
- Render the prompt as a collapsed full-width block (blockquote-style), long text collapsed by default.
- Render each tool call as a default-collapsed single row: status symbol, verb-led title, key params, derived duration; expand to typed detail (terminal output / inline diff / result summary).
- Show edit changed-file + `+N`/`−M` inline on the collapsed row; render failed calls whole-row red; render running calls as a one-line in-progress state.
- Merge consecutive exploratory calls into one expandable grouped row; render a lone exploratory call as a standard row.
- Expose a stable per-row identity + semantic state for #428/#429 to anchor on.

**Non-Goals:**
- No live-ticking duration or current-activity bottom bar (#428).
- No navigation sidebar / mini-timeline / error jump-to (#429).
- No session header or framework-shell change (#430); `SessionDetailShell` contract stays identical.
- No transcript data model, event protocol, or collection-pipeline change.
- No dedicated mobile layout (existing responsiveness must not regress).

## Decisions

### Decision 1: Pure presentation rewrite — keep the `DisplayTurn` projection, change only rendering

Reuse `projectTurn` / `projectSessionToDisplayTurns` as the projection boundary. The timeline components consume the same `DisplayTurn[]` they do today. The only model-touch is two narrow, behavior-driven corrections inside the projection (Decisions 7 and 8); no fields are added to `ToolPart`, `SessionTurn`, or the event protocol.

**Alternatives considered:**
- *Introduce a new `timeline` view model alongside `chat`/`timeline`/`compact` in `entities/session/model/view/*`.* Rejected — those view models are a separate (older) projection path not used by the widget that actually renders the page; building a parallel model doubles the surface to keep in sync and breaks the "data is unchanged" invariant for no gain.
- *Precompute a row-shaped view model (e.g. `TimelineRow[]`) from `DisplayTurn`.* Rejected — `DisplayAssistantPart` is already a discriminated row union (`text`/`tool`/`context-group`/…). An extra mapping layer adds churn without clarifying behavior.

### Decision 2: Single full-width column; defer the TOC to #429

Replace the `lg:grid lg:grid-cols-[1fr_180px] lg:max-w-4xl lg:mx-auto` shell in `SessionTranscriptLayout` with a single full-width column (uniform horizontal padding, no `max-width` cap). Remove the desktop `TurnTocRail` and the mobile `TranscriptToolbar` "Turns" dropdown — both are table-of-contents navigation, owned by #429. Retain the copy-full-text affordance (`CopyFullTextButton`) as a lightweight control at the top of the column. Keep `useTurnKeyboardNav` + the `turnRefs` map and the existing `data-turn-id`/`data-turn-ref` attributes on each turn — this is the stable turn-level locatable structure #429 mounts on.

**Rationale.** The TOC rail's 180px column is precisely what prevents a full-width single column; the issue's target mock has no rail. Navigation is an explicit Non-Goal (#429), so removing it now and letting #429 reintroduce a purpose-built nav is cleaner than carrying a half-width rail that violates the timeline invariant.

**Alternatives considered:**
- *Keep the rail but make the timeline full-width beside it.* Rejected — leaves the reading column < full width and preserves a nav surface #429 will redesign anyway.
- *Move the TOC into a collapsible overlay independent of the content grid.* Deferred to #429; doing it now is scope creep against the Non-Goals.

### Decision 3: Full-width turn divider bar replaces the right-aligned `TurnHeader`

Replace `TurnList`'s `TurnHeader` (currently `flex justify-end`, "Turn N · time") with a full-width divider bar: a top border spanning the column, carrying `Turn {ordinal} · {PromptKind label} · {start time}` always, plus `· {duration}` when `turn.completedAt` is set. The `PromptKind` label reuses the existing `KIND_LABELS` map (`PromptBlock.tsx` / `TurnToc.tsx` already define identical maps — consolidate into one shared map). Duration is derived (Decision 6).

### Decision 4: Prompt renders as a collapsed full-width block, not a bubble

Rework `PromptBlock`: remove `flex justify-end` + `rounded-2xl … bg-gray-200` bubble styling; render full-width with user-input (blockquote-style) semantics. Keep the existing collapse behavior (prompt text already defaults to collapsed via `useState(false)`, behind a "Show full prompt" affordance) — only the container styling and alignment change. A short prompt may render inline; a long prompt stays collapsed by default.

### Decision 5: Tool row is a default-collapsed single line with verb-led title, duration, and stable identity

Rework `ToolRowView` (`ui/tool-views/index.tsx`) so the collapsed row is one line composed of: `ToolStatusDot` (existing), a **verb-led title**, key params (existing `getToolDisplayArgs` badges), inline edit stats (Decision 7), and a **derived duration** (Decision 6) at the right. Typed detail (`renderSemanticContent`: `BashContentView` / `DiffContentView` / `ReadContentView` / `SearchContentView` / `TodoContentView` / `DelegationContentView`) stays hidden behind the existing expand chevron — i.e. default-collapsed, expand-on-demand. The verb-led title is composed from status + `normalizedName`:

| family (`normalizedName`) | running | completed | failed |
|---|---|---|---|
| `edit`/`write`/`apply_patch` | Editing `{file}` | Edited `{file}` | Failed to edit `{file}` |
| `bash` | `$ {cmd}` | `$ {cmd}` | `$ {cmd}` |
| `read`/`glob` | Reading `{path}` | Read `{path}` | Failed to read `{path}` |
| `grep`/`search` | Searching `{query}` | Searched `{query}` | Failed to search `{query}` |
| other | `toolName…` | `toolName` | `toolName` (failed) |

The recognizable target (`{file}`/`{cmd}`/`{query}`/`{path}`) reuses the existing extractors in `transcript-tool-utils.ts` (`getToolLabel`, `getFilePathFromInput`). Verb mapping is a new small table in the row layer; the noun-based `DISPLAY_TITLES` used by detail views is left untouched.

Each tool row root SHALL carry `data-tool-call-id={toolCallId}` and `data-tool-state={status}` (in addition to the existing `data-testid="tool-row"`/`data-tone`). These are the stable, locatable, semantic anchors for #428 (live activity) and #429 (navigation/error-jump).

**Whole-row red on failure.** When `part.hasError` / `status === 'failed'`, the row root gets a danger background+text class on the whole row (not just a border + "failed" label as today).

**Running state.** `running`/`pending` rows render the verb-led in-progress line (`ToolStatusDot` already animates for running). No live timer (Non-Goal → #428).

### Decision 6: Derive duration from timestamps via one shared helper; do not add a model field

`ToolPart.tool` and `DisplayToolPart` carry `startedAt`/`completedAt` (ISO strings) but no `duration`. Add a single shared `format-duration.ts` util: `formatElapsed(startedAt, completedAt): string | null` (returns `null` while `completedAt` is unset) plus the existing `formatDuration(ms)` semantics. Use it for both tool-row duration and turn-divider duration. Delete the two duplicate `formatDuration` copies in `ToolCallCard.tsx` and `SessionDetailShell.tsx`, pointing them at the shared util.

**Alternatives considered:**
- *Add `durationMs` to `DisplayToolPart` computed in `projectTurn`.* Rejected — it duplicates data already present as two timestamps, and the display-only derivation is trivially pure. Keeping it out of the model honors the "no data-model change" invariant most strictly.

### Decision 7: Edit changed-file + `+N`/`−M` shown inline on the collapsed row

The collapsed edit row SHALL show, without expanding: single-file → file path + `+N` `−M` (from `part.changedFiles[0].additions/deletions`, falling back to `parseEditWriteChanges`/`parsePatchOperations` already used by `EditToolCard`); multi-file → `{n} files changed` with the list behind expand. This promotes the `displayChangedFilesInline` summary that `ToolRowView` already computes (currently just file name/count) to include the line stats.

### Decision 8: Context grouping requires a run of ≥2; lone exploratory call renders as a standard row

Today `projectTurn`'s `flushContextGroup` wraps even a single exploratory call into a `context-group` part. Change the flush so that when the flushed stack has exactly one tool, it is pushed as a plain `tool` part instead; only runs of **≥2** consecutive exploratory calls become a `context-group`. `ContextGroupView` becomes a one-line collapsed grouped row whose summary reuses the projection-built title (per-type counts), and expands to the individual `ToolRowView`s. Rename the summary prefix from "Gathering context" to "Explored" to match the issue's verb-led mock. A group containing a failed call signals it on the collapsed summary line (danger tone / "failed" label), as today.

**Rationale.** The spec (`transcript-context-grouping`) explicitly requires a lone exploratory call to render as a standard row. The `≥2` guard belongs in the projection (not the view) because whether something is a group is a property of the part sequence, not of rendering.

**Alternatives considered:**
- *Keep grouping single calls and hide the affordance in the view.* Rejected — leaks a "group of one" into the DOM and into #429's anchoring model, and contradicts the spec.

## Risks / Trade-offs

- `[Large regression surface across every tool-type render path]` -> The widget has extensive render specs (`ui/*.test.tsx`: tool-labels, file-tools, context-tools, todo-tools, raw-tool-payload, turn-file-changes, states-and-turns, accessibility, shared-tool-semantics) that assert today's bubble/card DOM. Mitigation: migrate these specs in lockstep with the components (see Migration Plan); add focused new specs for full-width invariant, divider content, inline `+N`/`−M`, whole-row red, and group summary. The risk is rated medium precisely because the data model is unchanged — only DOM assertions regress.
- `[Removing the TOC rail reduces navigation until #429 lands]` -> Accepted trade-off. Keyboard turn nav (`useTurnKeyboardNav`) and turn refs are preserved, and `Cmd/Ctrl+arrows`-style jumping still works; the visible TOC list is intentionally deferred to #429.
- `[Verb-led titles may mismatch the recognizable target for unknown/exotic tools]` -> The "other" row in the verb table falls back to `toolName`; the existing `inferToolName`/`normalizeToolName` heuristics still feed the family lookup, so exotic tools degrade to a recognizable name rather than an empty row.
- [`≥2` grouping guard changes part-shape observed by other consumers]` -> `DisplayContextGroupPart` is consumed only within this widget; confirmed no other import of `projectTurn`'s grouping outside `widgets/session-transcript`. Still, re-run the context-tools and turn-file-changes specs.
- `[Duration derivation depends on clock timestamps from events]` -> If `completedAt` is missing/zero, `formatElapsed` returns `null` and the row simply omits the duration rather than showing `0s` or `NaN`.

## Migration Plan

1. **Projection (narrow):** add the `≥2` grouping guard + "Explored" prefix in `projectTurn`; add the shared `format-duration.ts` util.
2. **Layout/container:** rewrite `SessionTranscriptLayout` to a single full-width column; drop `TurnTocRail` + the toolbar TOC dropdown; keep `CopyFullTextButton`, `useTurnKeyboardNav`, `turnRefs`.
3. **Turn-level:** rewrite `TurnHeader` → full-width divider bar; rewrite `PromptBlock` → full-width collapsed block; remove the `max-w-[80%]` cap in `AssistantTextPartView`.
4. **Tool rows:** rewrite `ToolRowView` (verb title, duration, inline edit stats, whole-row red, `data-tool-call-id`/`data-tool-state`) and `ContextGroupView` (one-line group row); align the typed content views (`tool-views/*.tsx`) to expand-on-demand.
5. **Cleanup:** delete the now-unused `TurnToc.tsx`/`TranscriptToolbar.tsx` (if not referenced elsewhere) and the duplicate `formatDuration` copies.
6. **Tests:** migrate existing `ui/*.test.tsx` to the timeline/row DOM; add new specs for the four new behaviors. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
7. **Deploy/rollback:** frontend-only; normal web build. Rollback is reverting the commits — no data or config migration, no API change.

## Open Questions

- Should the mobile "Turns" dropdown be preserved as a temporary navigation crutch until #429, or dropped now for consistency? Lean: drop now (it is navigation, and #429 is the planned owner); revisit if dogfooding shows a regression in finding turns on long sessions.
- Exact collapse threshold / heuristic for "long prompt" vs "short prompt" inline display — current behavior collapses all prompt text by default, which already satisfies the spec; tuning the short-prompt inline case can follow from dogfooding.
