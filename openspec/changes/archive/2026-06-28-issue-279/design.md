## Context

The Epics list page (`packages/web/src/pages/epics/ui/EpicListPage.tsx`) currently buckets epics by lifecycle status only: one undifferentiated `Active` section for every `idle` + `running` epic (`EpicListPage.tsx:223`), plus `Paused`, `Done`, `Closed`. With Epic self-driving now landed (start/pause/resume/auto-advance, running-but-idle observability), `Active` collapses four very different situations — executing, startable, waiting, idle-empty — so users cannot scan the list to decide what to look at first.

The list endpoint already returns everything needed to disambiguate these states on `EpicWithProgress.progress` (`packages/web/src/entities/epic/model/types.ts:43`): `activeIssues`, `nextIssue`, `nextIssueReason`, `readyToMarkDone`. No backend, domain-state, lifecycle, auto-advance, or query change is involved. This is a presentation-layer refactor of a single page plus its tests.

Stakeholders: any user opening the Epics list to triage what to advance next. Risk driver (per issue): list grouping + action-semantics changes influence user workflow decisions, but ride entirely on existing read-model facts.

## Goals / Non-Goals

**Goals:**
- Split the `Active` bucket into four first-match-cascade presentation groups — `Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty` — computable solely from `EpicWithProgress.progress`.
- Render the four active groups top-to-bottom in that fixed priority order; keep `Done`/`Closed` folded by default and the four active groups expanded by default.
- Surface the right fact on each card: current in-progress issue (Running), next issue + `Start next issue` action (Ready to start), `nextIssueReason` (Waiting / Blocked), `Ready to mark done` / empty-state (Idle / Empty).
- Relabel the per-card manual control `Start` → `Start next issue` and gate it to Ready-to-start cards only, so it cannot be mistaken for the epic-lifecycle `Start Epic`.
- Keep the page free of horizontal overflow at 320 / 390 / 430 px and keep status badge + current/next issue number visible.

**Non-Goals:**
- No new epic domain state, lifecycle transition, auto-advance rule, or list-query change.
- No call into the epic detail-enrichment path (`EpicDetail`/`LinkedIssue.canStart`) to compute groups.
- No list-query performance work, no new filter/search system.
- No change to `Paused` rendering or positioning (left as-is).
- No change to the epic detail page's start/lifecycle controls.

## Decisions

### D1. Group via a pure cascade selector, not inline filters
Compute groups with a single pure function `groupActiveEpics(epics: EpicWithProgress[]): { running, readyToStart, waitingBlocked, idleEmpty }` placed next to the page (e.g. `EpicListPage.tsx` or a sibling `groupActiveEpics.ts`), rather than scattering four `.filter(...)` calls in the component.

Cascade (first match wins; an epic lands in exactly one group), mirroring `specs/epic-list-presentation/spec.md:5`:
1. `progress.activeIssues.length > 0` → `running`
2. `progress.nextIssue != null` → `readyToStart`
3. `progress.nextIssueReason != null` → `waitingBlocked`
4. else → `idleEmpty`

**Rationale:** a pure selector is unit-testable in isolation, keeps the cascade precedence explicit in one place, and makes the "active-issue check beats nextIssueReason" rule (`spec.md:40`) impossible to get wrong in JSX.

**Alternative considered:** four inline `epics.filter(...)` calls in `EpicListPage`. Rejected — duplicates the precedence logic and is easy to drift out of sync; also harder to test the cascade invariants directly.

### D2. Group on `nextIssue` presence, not `CanStart`
The proposal mentioned "`nextIssue` 且 `CanStart`" for Ready-to-start, but `CanStart` is not a field on the list read model (`EpicProgress`, `types.ts:43`) — it lives only on `LinkedIssue` (`types.ts:72`) inside the detail-enrichment path. Invoking that path would violate the "list read model only" constraint (`spec.md:47`). We therefore define Ready-to-start as "has a queued `nextIssue`", trusting the server's next-issue selection to have already excluded non-startable issues (a draft/external-prerequisite-blocked next issue surfaces as `nextIssue == null` + a `nextIssueReason`).

**Rationale:** keeps the grouping zero-cost and detail-path-free, which is the whole reason this change is low-risk.

**Alternative considered:** enrich the list response with per-issue `canStart`. Rejected — out of scope (would be a server/API change, explicitly excluded by the proposal) and unnecessary given the server already encodes startability via `nextIssue` nullness + reason.

### D3. Gate `Start next issue` to Ready-to-start cards only
The manual control appears exclusively on `Ready to start` cards. Running cards with both an in-progress issue and a `nextIssue` (e.g. the `activeWithBoth` test fixture) move to `Running` and **lose** their inline Start button. This is a deliberate behavior change from the current implementation (`EpicListPage.tsx:78`, which renders Start whenever `nextIssue` is present).

**Rationale:** the spec mandates the control only on Ready-to-start (`spec.md:102`, `spec.md:107`); showing two simultaneous actions (in-progress + start-another) on one card is exactly the confusion the issue calls out. Starting a further issue remains available on the epic detail page.

**Alternative considered:** keep the Start button on Running cards that also have a nextIssue. Rejected — contradicts the spec and re-introduces the ambiguity this change fixes.

### D4. Reuse `EpicSection` for all four active groups + Done + Closed
Render each of the six sections via the existing collapsible `EpicSection` (`EpicListPage.tsx:180`), passing `defaultExpanded` per group (active four = `true`, Done/Closed = `false`). `Paused` stays as the current inline block between the active groups and Done.

**Rationale:** `EpicSection` already encapsulates expand/collapse + count + test-id prefix; reusing it gives uniform a11y (`aria-expanded`) and keeps the change small.

**Alternative considered:** fold `Paused` into `EpicSection` too. Rejected — out of scope (spec is silent on Paused) and would add unrelated test churn.

### D5. Per-group card content, branching on group rather than re-deriving status
Pass the group identity (or a small render directive) into `EpicCard` / `statusText` so the card renders exactly the content the spec requires per group, instead of the current status-based branching (`EpicListPage.tsx:48`) which re-derives the same facts the cascade already classified. Concretely:
- Running → in-progress issue number + title (`spec.md:72`).
- Ready to start → next issue number + title + `Start next issue` button (`spec.md:96`).
- Waiting / Blocked → `nextIssueReason` text (`spec.md:77`).
- Idle / Empty → `Ready to mark done` when `readyToMarkDone`, else `No linked issues` (`spec.md:82`, `spec.md:87`).

**Rationale:** the cascade already decided the group; re-deriving inside the card would duplicate the precedence logic and risk divergence (e.g. showing a Start button on a Running card). Done/Closed keep their existing terminal text.

**Alternative considered:** keep the current status-only card and just change grouping. Rejected — the current card conditionally renders Start for *any* nextIssue, which D3 must remove, so the card must become group-aware anyway.

### D6. Section + test-id scheme
Section test-ids: `epic-section-running`, `epic-section-ready`, `epic-section-waiting`, `epic-section-idle`, retaining `epic-section-done` / `epic-section-closed`. The legacy `epic-section-active` is removed. Card-level test-ids (`epic-card-in-progress`, `epic-card-next`, `epic-card-ready`, `epic-card-start`) are retained so existing assertions can be adapted rather than rewritten.

**Rationale:** stable, cascade-aligned ids let tests assert both grouping membership and ordering (`spec.md:58`, `spec.md:63`) by reading heading/section order.

### D7. Mobile: enforce wrap, forbid fixed/min widths
Guarantee no horizontal overflow by construction: every flex row already using `min-w-0` is audited; the `Start next issue` row uses `flex-wrap` / stacks the button under the next-issue text on narrow widths; long titles and reasons use `break-words`/`line-clamp` rather than `truncate` for the state-bearing strings (status badge + issue number must stay visible per `spec.md:128`). Badges and the progress bar are flexible-width (`w-full`, no `min-w-[...]`).

**Rationale:** the spec's mobile requirement (`spec.md:118`) is a hard layout invariant; the safest way to satisfy it without a real browser in CI is to make overflow structurally impossible (wrap + flexible widths) rather than to tune pixel values.

## Risks / Trade-offs

- **[Behavior change] Running epics with a queued next issue lose their inline Start button** (D3) -> Mitigation: intentional per spec; starting further issues stays available on the epic detail page; call this out in the change description and release notes.
- **["Ready to start" label may over-promise]** The group really means "has a queued next issue"; if the server ever returned a `nextIssue` that is in fact not startable, the label would mislead (D2) -> Mitigation: the server's next-issue selection already encodes startability as `nextIssue == null` + reason, so this is bounded; flagged as an open question if selection rules change.
- **[jsdom cannot verify real overflow]** The mobile-no-overflow requirement (`spec.md:122`) cannot be truly asserted in jsdom, which does not compute layout -> Mitigation: tests assert (a) the absence of fixed/min-width patterns and presence of wrap classes on state-bearing elements, and (b) stubbed `documentElement.scrollWidth`/`clientWidth` returning equal values while rendering epics across all four groups; supplement with manual visual QA at 320/390/430 px.
- **[Existing tests reference `epic-section-active` and the unconditional Start button]** `EpicListPage.test.tsx` currently asserts `Active (...)` heading and Start-on-Running behavior -> Mitigation: rewrite those suites to the new four-group scheme and the Ready-to-start-only Start control as part of this change (tests are in-scope per proposal).
- **[Cascade precedence is easy to misread]** An epic with both `activeIssues` and `nextIssueReason` must land in Running, not Waiting/Blocked (`spec.md:40`) -> Mitigation: D1's single pure selector with explicit ordered checks + a dedicated unit test for the precedence scenario.

## Migration Plan

Single-PR, frontend-only (`packages/web`). No backend, DB, API, or domain migration; no data shape changes.

1. Add the pure `groupActiveEpics` cascade selector + its unit tests (D1).
2. Refactor `EpicListPage` to consume the selector and render six `EpicSection`s in fixed order (D4, D6); leave `Paused` inline.
3. Make `EpicCard` group-aware; implement per-group content and gate `Start next issue` to Ready-to-start (D3, D5); relabel `Start` → `Start next issue`.
4. Apply the mobile wrap/no-fixed-width treatment (D7).
5. Update `EpicListPage.test.tsx` to the new groups, ordering, card content, `Start next issue` gating, Done/Closed folded, and the (stubbed) no-overflow assertion.
6. Verify: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` pass.

**Rollback:** revert the PR. No state to migrate, no schema to undo; the list endpoint contract is unchanged.

## Open Questions

- Should the `Running` card also display `nextIssueReason` when an in-progress issue is itself blocked/waiting? The spec scopes the waiting-reason display to the Waiting/Blocked group only (`spec.md:77`), so this design omits it — confirm during implementation review whether a muted secondary line is wanted.
- Do we want a future server-side guarantee (contract test) that `nextIssue` is non-null only when the issue is genuinely startable, to harden the D2 approximation? Out of scope here but worth filing if the next-issue selection rules are ever relaxed.
