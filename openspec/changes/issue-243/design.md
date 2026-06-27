## Context

The session transcript page (`packages/web/src/pages/session/ui/SessionPage.tsx`) renders across four branches: incomplete/missing, waiting (running, 0 turns), empty (terminal, 0 turns), and the main transcript view (turns > 0). The first three branches all render `<SessionHeader>` (defined at `SessionPage.tsx:267`) carrying the issue breadcrumb, session title, status badge, stage, and turn count, with `recoveryBar` passed in as a sub-region slot. The main branch (`SessionPage.tsx:759-789`) does not — it renders a standalone `<div>{recoveryBar}</div>` strip and hands the rest to `<SessionTranscriptLayout>`, which internally renders its own `<StickySessionTitle>`. Result: on the primary reading path the user loses the breadcrumb, status, stage, and issue link; the only always-visible context is the in-stream sticky title.

The transcript itself is read-only and streams via `useSessionTranscript` (`scrollContainerRef`, `newContentAvailable`, auto-stick-to-bottom already exist on `SessionPage`). `displayTurns` is recomputed every render from `turns.map(projectTurn)` (`SessionPage.tsx:555`), and `DisplayTurn` already carries `id`, `startedAt`, `completedAt`, plus `prompt.kind/title/subtitle/sentAt` (`session-transcript-display.ts:107-115`). All data required for turn timestamps, TOC entries, and full-text serialization is already projected; this change is presentation-only.

Markdown is rendered through two paths today: a rich shared `MarkdownReader` (`shared/ui/markdown-reader/MarkdownReader.tsx`, with copy-code, TOC, attachment handling) and a bespoke minimal `<Markdown>` inside `AssistantParts.tsx:31-51`. Neither highlights code. Pipeline is `react-markdown@10` + `remark-gfm@4`; no highlighter is installed.

Constraints:
- Web-only change. Server / Runner / CLI / API / persistence untouched.
- Reactive to streaming: new turns arrive via SignalR without a page reload; any TOC, shortcut target, or copy output must track `displayTurns` without remounts.
- `AGENTS.md` testing rules: fakes only (no real clipboard/IO/IntersectionObserver), fast tests. `SessionPage.test.tsx` already mocks `navigator.clipboard` and `Element.prototype.scrollTo`.

Stakeholders: end users reading agent sessions on desktop and mobile.

## Goals / Non-Goals

**Goals:**
- Main transcript branch (turns > 0) always renders `SessionHeader` above the transcript, matching the empty/waiting/missing branches, with `recoveryBar` as a header sub-region.
- Each turn shows a turn-level timestamp.
- A TOC lists every turn and jumps to the chosen turn; updates as turns stream in.
- `j` / `k` / `g` / `G` keyboard navigation scoped to this page; defers to any focused text input.
- Fenced code blocks render with syntax highlighting on top of the existing markdown pipeline, without altering non-code content.
- "Copy full text" action writes the whole transcript as plain text; surfaces success and failure.
- No horizontal overflow and no broken header wrapping on 320–430 px viewports.

**Non-Goals:**
- Deleting the legacy `SessionTranscriptView.tsx` (619 lines, only referenced by tests). Out of scope; tracked separately as architecture cleanup.
- Merging the bespoke `AssistantParts` markdown path into the shared `MarkdownReader`. We only add highlighting to it; full unification is deferred.
- Real-time co-reading / multi-user presence.
- Session editing, inline turn actions, or per-turn pinning/bookmarking.
- Touch gestures, custom keymap configuration, or a settings UI for shortcuts.
- Reflowing diff/patch blocks or rewriting `ToolCallCard`.

## Decisions

### D1. Render `SessionHeader` on the main branch; drop `StickySessionTitle`.

Replace the standalone `<div>{recoveryBar}</div>` at `SessionPage.tsx:761-763` with the same `<SessionHeader issueNumber issueTitle meta statusKind turnCount recoveryBar />` already used by the other three branches (lines 709, 725, 746). The header sits in the non-scrolling region above `scrollContainerRef`, so it is always visible without stickiness tricks.

Remove `<StickySessionTitle>` from `SessionTranscriptLayout.tsx:47` (and its file). It was a workaround for the missing header and now duplicates the page header. The layout becomes a pure turn list + streaming indicators.

- *Alternatives considered:*
  - Make the page header `sticky` inside the scroll container and keep `StickySessionTitle`. Rejected: two stacked titles, double maintenance, and the spec requires the rendered header to *match* the empty/waiting header.
  - Keep `StickySessionTitle` as a compact summary that appears only after the main header scrolls out. Rejected: requires an additional scroll-spy/`IntersectionObserver` for fade-in, adds complexity for no information gain since the full header is always visible in the non-scrolling region.

### D2. Turn timestamp lives on `TurnItem`, sourced from `DisplayTurn.startedAt`.

Add a one-line turn header above `PromptBlock` inside `TurnItem` (`TurnList.tsx:29`) showing the turn index and `new Date(turn.startedAt).toLocaleTimeString()`. Use `startedAt` (always present); `completedAt` is left for the streaming indicator that already exists. Format matches the existing `formatTime` helper used in `AssistantParts.tsx:7`.

- *Alternatives considered:*
  - Add a `timestamp` field to `DisplayTurn` in the model. Rejected: `startedAt` already is the timestamp; adding a derived field duplicates the source of truth.
  - Render the timestamp inside `PromptBlock`. Rejected: prompt kinds like `legacy-missing` already render their own `sentAt` and would double-show.

### D3. TOC is a right rail on `lg+` and a header dropdown below `lg`.

Render `<TurnToc>` from the same `displayTurns` array consumed by `<TurnList>`, so streaming additions appear with no extra wiring. Each entry shows the 1-based index and a short label derived from `prompt.kind` (e.g. "Follow-up", "Initial Task") plus `prompt.title?.slice(0,60)`. Clicking calls `scrollIntoView({ behavior: 'smooth', block: 'start' })` on the turn's element ref.

Placement:
- At `lg` and above: a sticky right rail inside the transcript scroll area, `max-w-2xl` layout shifts to a two-column `lg:grid-cols-[1fr_180px]` so the transcript keeps its current measure and the rail sits in the gutter.
- Below `lg`: a compact "Turns ▾" disclosure button in a slim `TranscriptToolbar` above the turn list. Opens an overlay list reusing the same `<TurnTocList>`.

A `Map<number, HTMLDivElement>` of turn refs keyed by 1-based index (not by `turn.id`) is stable across re-renders as new turns stream in. The map is owned by `SessionTranscriptLayout` and passed to both `<TurnList>` (registers refs) and `<TurnToc>` / keyboard hook (reads refs).

- *Alternatives considered:*
  - Anchor `#turn-N` links with `id` and `scrollIntoView` via fragment navigation. Rejected: competes with React Router's history and is awkward inside a scroll container (the browser scrolls the window, not `scrollContainerRef`).
  - Always-visible top TOC strip. Rejected: consumes vertical space on every render and competes with the always-visible header.
  - Hide the TOC on mobile entirely. Rejected: the spec requires the TOC to list every turn with no viewport carve-out.

### D4. Keyboard navigation via a `useTurnKeyboardNav` hook; current turn derived from scroll position.

Add `useTurnKeyboardNav({ scrollContainerRef, turnRefs, turnCount })` in `widgets/session-transcript/model/`. It:
1. Attaches one `keydown` listener on `scrollContainerRef.current` (falls back to `window`) on mount; detaches on unmount. Scope is naturally limited because the listener is mounted only while the session page is alive.
2. Bails out when `document.activeElement` matches `input, textarea, select, [contenteditable="true"], [data-composer-input]` — guarantees the followup composer and any other input never get hijacked.
3. Computes the current turn index on demand by scanning `turnRefs` for the last element whose `getBoundingClientRect().top` is at or above a threshold (e.g. 120 px from the top of the scroll container). No `IntersectionObserver`, so no jsdom polyfill is required for tests.
4. `j` → scroll to ref at `index + 1`; `k` → `index - 1`; `g` (no shift) → ref at `0`; `G` (`e.key === 'G'` or `e.shiftKey && e.key === 'g'`) → ref at `turnCount - 1`. Boundary clamped; out-of-range is a no-op so the browser does not scroll past the last turn.
5. Calls `scrollIntoView({ block: 'start' })` and lets the existing `useSessionTranscript` `setIsNearBottom` logic reconcile the "jump to bottom" button.

`g` vs `G` is read from `event.key` (lowercase 'g' vs uppercase 'G'); no separate timer-based "press g twice" scheme, matching the spec's `g` = top, `G` = bottom.

- *Alternatives considered:*
  - `IntersectionObserver`-based scroll-spy. Rejected: jsdom does not implement it, tests would need a polyfill/fake, and we only need the index at keystroke time, not continuously.
  - A global hotkey library. Rejected: 4 keys do not justify a dependency, and scoping is simpler with a local listener.

### D5. Syntax highlighting via `rehype-highlight`; wired into a shared `TranscriptMarkdown`.

Add `rehype-highlight` (built on `lowlight`/`highlight.js`, ESM, ~30–50 KB gzipped with common grammars) as a `rehypePlugins` entry. Introduce `widgets/session-transcript/ui/TranscriptMarkdown.tsx` — a thin wrapper around `react-markdown` + `remark-gfm` + `rehypeHighlight` with the same `code`/`pre` styling currently in `AssistantParts.tsx:31-48`, plus `[overflow-wrap:anywhere]` and `overflow-x-auto` to prevent narrow-viewport overflow. Use it in `AssistantTextPartView` in place of the inline `<Markdown>`.

Theme: bundle a single scoped CSS import (e.g. `highlight.js/styles/github.css`) keyed under a `.transcript-md` wrapper so it does not collide with `MarkdownReader`'s styles. Light theme only for v1; the app has no dark mode.

Why not the shared `MarkdownReader`? It is far richer (attachments, collapse, TOC, copy-code) and is the right eventual home, but swapping it in changes prompt-card rendering and risks regressions across the whole transcript. D5 deliberately keeps the surface small; the unification is recorded as an Open Question.

- *Alternatives considered:*
  - `shiki` / `rehype-pretty-code`. Rejected: WASM/grammar payload, async highlighting, designed for build-time or SSR. Wrong fit for a client-rendered streaming transcript.
  - `prism-react-renderer`. Rejected: not a `react-markdown` rehype plugin — would require a custom `code` component and manual language mapping, more code for the same outcome.
  - Apply highlighting inside `MarkdownReader` only. Rejected: the transcript's assistant text does not currently go through `MarkdownReader`, so this would not satisfy the spec for the transcript view.

### D6. "Copy full text" via a model-layer serializer + existing clipboard pattern; failures surfaced via `sonner`.

Add `serializeTranscriptPlainText(turns: DisplayTurn[]): string` to `widgets/session-transcript/model/`. For each turn in order, emit a header line (e.g. `== Turn N · <kind> · <timestamp> ==`), the prompt title/subtitle/text, then each assistant part: text parts in full; reasoning summarized as `[reasoning omitted, X KB]` (consistent with the "reasoning collapsed by default" requirement); tool parts as `[tool <name>] <title>`; errors as `[error] <message>`. Joined by blank lines.

UI: a `CopyFullTextButton` in the new `TranscriptToolbar` (next to the mobile TOC trigger). Disabled when `turns.length === 0` (satisfies "copy unavailable for empty transcript"). On click:
1. Compute the serialized text (memoized on `turns` reference).
2. Call `navigator.clipboard.writeText(text)`.
3. On resolve: flip local state to "Copied!" for 2 s (matches existing pattern at `PromptBlock.tsx:31`, `AssistantParts.tsx:24`).
4. On reject or when `navigator.clipboard` is absent: keep button label stable, fire `toast.error(...)` via `sonner` (already a dependency), and set `data-state="failed"` for tests. Never report success on failure.

Reuse the robust existence check from `ArtifactContentViewer.tsx:89` rather than the bare `.then(...)` used elsewhere.

- *Alternatives considered:*
  - Include full reasoning text in the copy output. Rejected: contradicts the "reasoning collapsed by default" reading model and would produce massive clipboard blobs for thinking-heavy sessions.
  - Use the existing per-part copy buttons' pattern verbatim (no failure handling). Rejected: the spec explicitly requires failure to be surfaced.
  - Put the button inside `SessionHeader`. Rejected: would require threading `turns` into the header and re-checking empty-state across all four branches. The toolbar lives only on the main branch where turns exist.

### D7. Responsive fixes are className audits, not redesigns.

Targeted changes to keep 320–430 px usable:
- `SessionHeader` outer rows: switch the metadata cluster from `flex-wrap justify-end` to a vertical stack below `sm`, cap `issueTitle` with `truncate`, ensure the back-arrow + `Issue #N` row never wraps mid-link (`whitespace-nowrap` on the link, `min-w-0` + `truncate` on the title).
- `TurnList` and cards: replace `max-w-[80%]` / `max-w-[90%]` with `max-w-[90%] sm:max-w-[80%]` and add `min-w-0` on parents so truncation works.
- `TranscriptMarkdown` `pre`: `overflow-x-auto` plus `max-w-full` on the wrapper, matching `MarkdownReader`'s `[overflow-wrap:anywhere]` on inline code so long URLs no longer push width.
- TOC rail hidden below `lg`; mobile uses the dropdown (D3).

Add a Vitest+jsdom test asserting `scrollWidth <= clientWidth` is not violated given a representative long-line transcript. jsdom does not perform real layout, so the test fakes `scrollWidth`/`clientWidth` getters on the container — the value of the test is regression on the className contract (which classes are applied), not pixel measurement.

- *Alternatives considered:*
  - Playwright visual regression at 320/375/430 px. Rejected for v1: heavier CI footprint; the e2e suite already covers SessionPage liveness and we only need the layout contract. Can be added later.

## Risks / Trade-offs

- [`rehype-highlight` adds ~30–50 KB gzipped and a CSS theme] → Acceptable for a transcript-heavy app; load only on the session route via the existing dynamic import boundary if bundle analysis shows impact. Mitigation: import `lowlight` with a common-language subset (ts/js/bash/json/python/md/diff/yaml) via `lowlight/lib/common` rather than the full `highlight.js`.
- [`j`/`k`/`g`/`G` may conflict with browser or assistive-tech shortcuts] → Listener is scoped to the session page and bails out on any focused editable. `g` single-key is uncommon in browsers. Mitigation: also skip when `event.metaKey`/`event.ctrlKey`/`event.altKey` are pressed, so only bare keystrokes navigate.
- [Removing `StickySessionTitle` is a visible behavior change for users who relied on the compact sticky summary] → The always-visible page header carries strictly more information (breadcrumb, stage, usage, recovery actions). Mitigation: none needed; the page header is a superset.
- [TOC right rail reduces horizontal room for the transcript on `lg` (~1024 px)] → Rail is a 180 px gutter outside the existing `max-w-2xl` (672 px) column, so total ~880 px; fits within `lg`. Mitigation: rail becomes an overlay drawer if a future minimum width breaks this assumption.
- [`serializeTranscriptPlainText` output format is opinionated (reasoning omitted, tools summarized)] → Some users will want raw everything. Mitigation: keep the serializer pure and unit-tested so the format is easy to extend later; note as Open Question.
- [jsdom cannot measure layout, so the "no horizontal overflow" test is weak] → Mitigation: test asserts the responsive className contract (which classes apply at which breakpoints) rather than pixels; pair with a Playwright a11y/e2e run manually before merge.
- [`scrollIntoView` inside a nested scroll container has cross-browser quirks] → Mitigation: call `scrollIntoView({ block: 'start' })` on the turn element and let the browser reconcile; existing `useSessionTranscript` `setIsNearBottom` already reconciles post-scroll state. Add a test that mocks `scrollIntoView` and asserts it is called on the correct target ref.

## Migration Plan

This is a presentation-only web change with no API, persistence, or config surface — no data migration, no feature flag, no coordinated deploy.

Rollout:
1. Land D1 (header on main branch, drop `StickySessionTitle`) behind nothing — it is a strict improvement and the other three branches already render the header.
2. Land D2 + D5 + D7 together (timestamps, highlighting, responsive className fixes) — purely additive rendering.
3. Land D3 + D4 + D6 (TOC, keyboard, copy full text) — additive UI on the main branch only.

Each step is independently shippable and revertible. The three steps can be a single PR or split; no ordering hazard between them.

Rollback: revert the commit(s); no schema or API to unwind. The legacy `SessionTranscriptView.tsx` and `StickySessionTitle.tsx` (deleted in step 1) are recoverable from git history if a regression is found after merge.

Testing gates before merge (per `AGENTS.md`):
- `npm run typecheck -w packages/web`
- `npm run test:run -w packages/web` — new cases for: header on main branch, turn timestamp render, TOC entry count + scroll target, keyboard nav with and without focused input, copy success/failure/empty, mobile className contract.
- `npm run lint` if the web package exposes one (none configured — `tsc -b` is the gate).

## Open Questions

- Should the shared `MarkdownReader` absorb `TranscriptMarkdown` (D5) so the whole transcript renders through one pipeline? The issue body flags parallel render paths as architecture debt. Deferred here; revisit in a dedicated cleanup issue.
- Should "Copy full text" optionally include reasoning text, behind a modifier (e.g. shift-click) or a settings toggle? Default is "omitted"; decide based on user feedback.
- TOC entry label: today we plan to show `prompt.kind` + truncated `prompt.title`. For sessions where every turn has the same kind/title (e.g. all "Follow-up"), is the first line of the prompt text a better discriminator? Pending a quick visual check on real sessions.
- Should the keyboard shortcuts also support `Ctrl+d`/`Ctrl+u` (half-page jump) or `Space`/`Shift+Space` (page jump) for users coming from pager conventions? Not in spec; deferred.
- Do we need a visible hint/cheatsheet for the keyboard shortcuts (e.g. a `?` affordance), or is `j`/`k`/`g`/`G` discoverable enough? Not in spec; deferred.
