## Context

The Epic detail page (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`) is today a "document detail page": the header `Card` renders `epic.description` immediately after the title, and only then renders a 3-column grid (Progress / Next Issue / Current Activity). On mobile the long description pushes the status facts below the first fold, so a user opening an Epic to judge "is it advancing?" must scroll past background prose.

This no longer matches the Epic interaction model: start / pause / resume / auto-advance are all live, and lifecycle actions (Start Epic / Pause / Resume / Mark Done) are already wired to backend mutations. The page needs to become a "goal-status workbench" that surfaces progress, current activity and the next-step reason first.

Crucially, **all the read-side facts already arrive on the client** via `EpicDetail` (`packages/web/src/entities/epic/model/types.ts`):

- `progress`: `deliveredCount`, `totalIssueCount`, `blockedIssues`, `activeIssues`, `nextIssue`, `nextIssueReason` (free-form string), `readyToMarkDone`.
- `linkedIssues[]`: each carries structured `status`, `canStart`, `startBlocker` (`{kind:'draft'}` | `{kind:'waiting-for'; issue:{number,...}}`), `externalPrerequisites[]`, `health`.
- `status`, `pauseReason`.

The server `EpicDetailDto` and `EpicProgress.Build` (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs`) are unchanged by this work — see Non-Goals.

Constraint: prerequisite issue #277 (mobile horizontal-overflow fix) is `done`, so the new summary-first layout can rely on the page no longer overflowing horizontally.

Stakeholders: individual developers using the Epic detail page on desktop and mobile to decide whether an Epic is advancing and whether they need to act.

## Goals / Non-Goals

**Goals:**

- Reorder the Epic detail page into a summary-first information architecture: meta + lifecycle action + summary grid (progress / current activity / next issue-or-reason) render before the description, in the first fold on desktop and mobile.
- Demote the full description to a collapsible Overview/Description region after the summary; omit it entirely when empty.
- Surface a single prominent primary lifecycle action chosen by `(status, readyToMarkDone)` per the spec matrix.
- Render the disabled `Mark Done` with an on-screen visible reason (no `title`/hover tooltip), readable on touch devices.
- Present distinct advancement-status copy (running-but-idle, waiting-for-in-progress, draft blocker, external prerequisite blocker), with navigation links to the relevant linked issue where one exists.
- Cover paused-epic (pause reason + resume re-evaluation hint), idle-epic (startable next issue or why none), and terminal (no lifecycle action) states.
- Preserve all existing detail capabilities: linked-issue listing/editing/add, graph view, edit/close dialogs.

**Non-Goals:**

- No backend, API, persistence, domain, or DTO changes. Selection rules in `EpicProgress` / `SelectStartableNext` are untouched.
- No change to auto-advance behavior or the next-issue selection algorithm.
- No new Epic failure/health state.
- No mobile horizontal-overflow work (owned by #277, done).
- No server-side structured enum for `nextIssueReason` — see Decisions.

## Decisions

### D1. Pure web-layer change; derive display state client-side from `linkedIssues`, never by parsing `nextIssueReason`

The spec requires (a) distinct copy per advancement state and (b) a navigation link from a waiting reason to the relevant issue. The server `nextIssueReason` is a free-form, localized string (e.g. `"Waiting for #42 to complete"`, `"Still a draft: #8"`). Parsing it for issue numbers and state kind would be fragile and locale-coupled.

Instead, derive a structured `AdvancementState` discriminated union on the web from data that is already present on each `linkedIssue` — mirroring the existing `deriveReadiness()` helper in `packages/web/src/widgets/epic-dependency-graph/model/readiness.ts`, which already classifies a linked issue into `can-start | waiting | in-progress | done` and extracts `waitingForIssueNumber` from `startBlocker`.

```
type AdvancementState =
  | { kind: 'running-but-idle' }                 // running, no in-progress, no startable next
  | { kind: 'waiting-for-in-progress'; issueNumber: number }
  | { kind: 'draft-blocker'; issueNumber: number }
  | { kind: 'external-prerequisite-blocker'; issueNumber: number; prerequisiteNumbers: number[] }
  | { kind: 'idle-no-next'; reason: string }     // undelivered exist but none startable, no specific blocker
  | { kind: 'has-next'; issueNumber: number }    // next startable exists (link rendered)
  | { kind: 'nothing-pending' }                  // all delivered / readyToMarkDone
```

Derivation rules (display only, must not re-implement selection):
- An in-progress linked issue → `waiting-for-in-progress` (its `number` drives the nav link). This is the "running epic waiting for an in-progress linked issue to finish" state.
- Else if the highest-priority undelivered candidate's `startBlocker.kind === 'draft'` → `draft-blocker`.
- Else if that candidate has non-empty `externalPrerequisites` → `external-prerequisite-blocker` (use `externalPrerequisites[].number` for the nav link(s)).
- Else if a startable candidate exists (`canStart && !startBlocker && status==='backlog'`) → `has-next`.
- Else → `idle-no-next` with a derived reason, or `nothing-pending` when undelivered is empty.

The server-provided `progress.nextIssue` and `nextIssueReason` remain the source of truth for *what the next issue is*; the client derivation only enriches the *why* copy and the nav targets. `nextIssue` still drives the primary "next startable issue" link when present.

**Alternatives considered:**
- *Add a structured `NextIssueReasonKind` enum to `EpicDetailDto`.* Cleanest for the client, but it is a backend/DTO change explicitly excluded by the proposal, and would force the server to pick a single winner where the client already has the full per-issue picture.
- *Regex-parse `nextIssueReason`.* Rejected: locale/format coupling, breaks the moment copy changes, can't reliably distinguish draft vs external blocker.

### D2. Page restructure into three regions inside the existing layout

Keep the single `max-w-4xl` page shell and `Back to Epics` link. Reorganize content into:

1. **Header Card** (first fold): meta row (`#number`, status badge, priority badge, optional pause-reason chip), title, the prominent primary lifecycle action (D3), then the existing 3-column summary grid (`md:grid-cols-3`, stacks on mobile). The description is **removed** from this card.
2. **Overview/Description Card** (after summary): rendered only when `epic.description` is non-empty. Uses the existing `MarkdownReader` with `mode="collapsible"` (`packages/web/src/shared/ui/markdown-reader/MarkdownReader.tsx` already exposes `mode`, `collapsedHeight`, and `data-testid="markdown-collapse-control"` / `"markdown-expand-control"`). No new collapse primitive is built.
3. **Linked Issues Card** (unchanged): add-issue form, list/graph toggle, `LinkedIssueRow`s, dialogs — all preserved as-is.

This satisfies "summary before description on desktop", "summary reachable in the first fold on mobile" (description no longer precedes the grid), and "no Overview region when description is empty".

### D3. Single prominent primary lifecycle action via a pure selector

Introduce a pure selector `primaryLifecycleAction(status, readyToMarkDone): PrimaryAction | null` encoding the spec matrix exactly:

| status | ready | primary |
|---|---|---|
| idle | no | `Start Epic` |
| running | no | `Pause` |
| paused | any | `Resume` (Mark Done is NOT surfaced as primary) |
| idle/running | yes | `Mark Done` (replaces Start/Pause) |
| done/closed | — | none |

The chosen action renders as the prominent `Button` (default variant) at the top of the header; the status-based action it replaces is **not** also rendered, to honor "single prominent primary". `Edit` and `Close Epic` remain as always-available secondary (`variant="outline"`) actions — they are not part of the lifecycle-primary set.

`Mark Done` is additionally rendered for every non-terminal epic (even when it is not the primary), so that the "why can't it be done" reason is always discoverable:
- When `ready` and not paused → it is the enabled primary.
- Otherwise → rendered as a disabled secondary `Button` with an **on-screen visible reason** (D4), never as a hidden affordance.

Invoking any action calls the existing `useStartEpic` / `usePauseEpic` / `useResumeEpic` / `useMarkEpicDone` hooks; on success TanStack Query invalidation already refreshes epic + dashboard (no new wiring).

**Alternatives considered:**
- *Render all of Start/Pause/Resume/Mark Done simultaneously and just reorder.* Rejected: contradicts "single prominent primary" and the ready-epic / paused-ready-epic scenarios.
- *Hide Mark Done entirely unless it is primary.* Rejected: loses the "disabled reason" scenarios (paused epic, unfinished linked issues) the spec explicitly lists.

### D4. Disabled `Mark Done` shows an on-screen reason (no `title` tooltip)

Replace the current `title={markDoneTooltip}` hover tooltip with a visible `<p>`/chip placed directly under the action row whenever `Mark Done` is disabled, with `data-testid="mark-done-disabled-reason"`. Reason text derived from state:
- paused → "Resume this Epic before marking it done."
- unfinished remaining → `${unfinishedCount} linked ${unfinishedCount===1?'issue':'issues'} remain unfinished.`

This is readable without pointer hover (touch-safe) and is what the "Disabled Mark Done … on touch devices" scenarios assert on.

### D5. Summary sub-blocks reuse existing components; advancement copy in a new pure module

- `CurrentActivityList` / `CurrentActivityEntry` (already present) keep rendering active + blocked issues with nav links and the empty state. No change.
- Next-issue block keeps using `progress.nextIssue` for the link; the advancement-state copy (D1) is rendered alongside/within the Next Issue column as the "why" when no next issue is startable.
- The `AdvancementState` derivation + copy table lives in a new pure module under `packages/web/src/pages/epic-detail/model/` (e.g. `advancement.ts`), unit-tested in isolation (fast, no React) — consistent with how `readiness.ts` and `inline-start.ts` are structured in sibling features.

## Risks / Trade-offs

- **[Client-side advancement derivation duplicates the spirit of server `EpicProgress` logic → drift]** → Mitigation: the client derivation only produces *display copy and nav targets* from per-issue facts; it does **not** decide which issue starts (selection stays server-side). It is a pure function covered by unit tests asserting each `kind`. The server `nextIssue`/`nextIssueReason` remain authoritative for "what is next".
- **[External-prerequisite blocker has no dedicated `startBlocker` kind server-side]** → Mitigation: derive `external-prerequisite-blocker` from non-empty `linkedIssue.externalPrerequisites` on the candidate issue; this field is already populated by the server. If `externalPrerequisites` is empty but the issue is still non-startable, fall back to `idle-no-next` rather than inventing a blocker.
- **[Moving description below the fold may surprise users expecting a doc page]** → Mitigation: Overview region is expanded-by-default (only collapsed on demand via `MarkdownReader` collapsible mode), and is clearly labeled, so the description remains one tap away rather than removed.
- **[Single primary action removes simultaneous access to e.g. Pause when ready+running]** → Mitigation: acceptable per spec ("in place of"); `Close Epic` and `Edit` remain secondary; a running+ready epic is the case where the user's intent is to finish, not pause.
- **[Test surface is large (many status × readiness × advancement combinations)]** → Mitigation: pure selectors are unit-tested exhaustively and cheaply; the React integration tests focus on layout order, primary-action visibility, and disabled-reason visibility rather than re-asserting every matrix cell through the DOM.

## Migration Plan

This is a front-end-only change with no data or API migration.

1. Land D1 (`advancement.ts` + unit tests) and D3/D4 selectors behind the existing page, with the page still using the old layout — verify selectors with unit tests (`npm run typecheck -w packages/web`, `npm run test:run -w packages/web`).
2. Apply D2 page restructure (move description into collapsible Overview, reorder header card) and wire selectors into the header.
3. Add/adjust `EpicDetailPage.test.tsx` cases for: summary-before-description DOM order (desktop + mobile), collapsible Overview control, primary-action matrix, disabled-Mark-Done visible reason, advancement copy per kind, no-Overview-when-empty, no regression of linked-issue/edit/add capabilities.
4. Verify manually on desktop and a 390px mobile viewport that the summary grid is in the first fold and the description is below it and collapsible.
5. Typecheck + test the web package before handoff.

Rollback: revert the single page module (+ new `advancement.ts`); no backend, DB, or config to roll back. No feature flag is needed given the scope is one page and there is no persisted state change.

## Open Questions

- Should the collapsible Overview default to **expanded** (current plan) or **collapsed** for long descriptions to truly minimize the first fold? Defaulting to expanded preserves readability of background context; collapsing by default better serves "status workbench" but hides context on first visit. Leaning expanded-by-default pending review.
- For `external-prerequisite-blocker`, should the nav link point at the **epic-internal prerequisite** (when one exists among linked issues) or always at the external prerequisite issue? Current plan: link to the external prerequisite issue number from `externalPrerequisites`; revisit if prerequisites are commonly intra-epic.
