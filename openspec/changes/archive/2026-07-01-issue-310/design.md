## Context

`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx` is the worst complexity hotspot under the "代码复杂度热点治理" epic: scc Complexity 211 across 1375 lines, mixing five unrelated concerns in one component body:

- **9 inline presentational pieces** — `PriorityChip`, `WorkflowStagePill`, `HealthPill`, `DraftPill`, `ArchivedPill`, `WorkflowYamlDialog`, plus the helpers `WORKFLOW_STAGE_LABELS`, `stageToIssueStatus`, `formatRelativeTime`, `formatStageName`, `attachmentFromMetadata` (lines 29–205).
- **13 TanStack mutations** (`start`, `markReady`, `addPrerequisite`, `removePrerequisite`, `close`, `forceStop`, `stop`, `reopen`, `resume`, `retry`, `rerun`, `addComment`, `deleteComment`), most of which repeat `invalidateQueries(['issues'])` / `['agent-status']` (lines 255–393).
- **Two near-identical click-outside + 5 s auto-dismiss effects** — one for `forceStopConfirming` (222–235), one for `stopConfirming` (319–332).
- **A right-rail Actions card that is a ~300-line state machine** over `isArchived × isBacklog × isAgentRunningOnThis × recovery.allowedActions × workflowTimeline.availableActions × issue.health` (1003–1301).

Constraints / stakeholders:

- This is a **pure frontend, behavior-invariant refactor** (risk: low). No server / runner / CLI / API / routing / Query-key change.
- The page is guarded by **4 component suites (~71 tests)** in `ui/`: `IssueDetailPage.test.tsx` (incl. the issue-180 *density / whitespace rhythm* regression block at lines 762–891), `.archived`, `.capacity-gating`, `.readiness`. These are the regression net and **must pass unchanged**.
- The hardest invariant is the **density test** (`IssueDetailPage.test.tsx:793`): it asserts the main column carries `space-y-8`, the right rail `space-y-6`, the grid `gap-8`, frames `mb-8`, and that `grid.querySelector(':scope > .lg\\:col-span-2')` resolves. It also pins every major `data-testid`. Any extraction that changes *which element owns* a structural wrapper class or testid breaks it.
- Precedent in the repo: `pages/epic-detail/` already splits logic into `model/` (pure functions + hooks, each with a `.test.ts`) and keeps the page + component tests in `ui/`.

## Goals / Non-Goals

**Goals:**

- Collapse `IssueDetailPage.tsx` to **page-level orchestration** (data loading, layout slots, dialog mount) by extracting its five concerns into cohesive units.
- Merge the two duplicated click-outside effects into one reusable `useConfirmOutsideClick()` hook.
- Make the Actions-card state machine **testable without a DOM** by extracting its branching into a pure derivation.
- Preserve every `data-testid`, the rendered DOM/margins, TanStack Query keys, navigation URLs, and the issue-300 `isCapacityFull` derivation **bit-for-bit**.

**Non-Goals** (per proposal):

- No change to any user-visible interaction, navigation, or render output.
- No change to mutation call shapes or Query keys (`['issues']` / `['issues', issueNumber]` / `['agent-status']`).
- No performance work, no new dependencies, no server/runner/CLI changes.

## Decisions

### D1 — Layout: follow the `epic-detail` `model/` + `ui/` split

Extract pure logic/hooks into a new `pages/issue-detail/model/` (mirrors `epic-detail/model/`); keep the page and component tests in `ui/`; co-locate presentational pieces under `ui/`.

```
pages/issue-detail/
  model/
    useConfirmOutsideClick.ts          (+ .test.ts)
    useIssueDetailMutations.ts
    actionsState.ts                     (+ .test.ts)   # pure derivation for the Actions card
    format.ts                           # formatRelativeTime, formatStageName, stageToIssueStatus, WORKFLOW_STAGE_LABELS
  ui/
    IssueDetailPage.tsx                 # slimmed to orchestration
    IssueDetailPage.*.test.tsx          # unchanged
    pills.tsx                           # PriorityChip, WorkflowStagePill, HealthPill, DraftPill, ArchivedPill (one cohesive cluster)
    WorkflowYamlDialog.tsx
    sections/
      IssueDescriptionSection.tsx
      IssueDiffFilesSection.tsx
      IssueCommitsSection.tsx
      IssueCommentsSection.tsx
    cards/
      IssueDetailsCard.tsx
      IssueDriftCard.tsx
      IssueConfigurationCard.tsx
      IssueActionsCard.tsx
      IssuePrerequisitesCard.tsx
      IssueReadinessCard.tsx
```

**Alternatives considered:**

- *One file per chip.* Rejected — five <30-line pills don't each warrant a file; a single `pills.tsx` with five exports keeps the import surface small while still removing them from the god file. (This is the one place the proposal's "their own files" is read as "their own module" rather than literally one component per file.)
- *Everything flat in `ui/`.* Rejected — `sections/` + `cards/` subfolders communicate the two layout columns and prevent `ui/` from re-becoming a dump.
- *A `components/` folder.* Rejected — no other page uses that name; `model/`+`ui/` is the established convention.

### D2 — Preserve structural wrappers in the page; move only *content* into sections/cards

The density test (`IssueDetailPage.test.tsx:793–870`) queries the **structural** elements directly (`:scope > .lg\:col-span-2`, `issue-detail-right-rail`, frame testids with `mb-8`). Therefore:

- The page **keeps** the grid `<div data-testid="issue-detail-content-grid" className="… gap-8 …">`, both column wrappers (the `space-y-8` main column, the `space-y-6` right rail), and every `*-frame` wrapper div carrying `mb-8`.
- Section/card components render **only their inner content** and receive props; they do **not** re-introduce a wrapper that the density test keys on.

This is the single most important rule for keeping all 71 tests green: move JSX verbatim, never change which element owns a `mb-8` / `space-y-*` / `gap-*` class or a structural `data-testid`. Content-level testids (`description-section`, `commits-section`, `start-button`, …) travel with their JSX into the child component unchanged.

### D3 — Extract the Actions-card state machine as a pure `actionsState` derivation

The ~300-line Actions card (1003–1301) branches on six inputs. Move the **branching** into a pure function in `model/actionsState.ts`:

```ts
computeActionsState({ issue, agentStatus, workflowTimeline, recovery }) -> {
  showArchivedNote, startVariant, showForceStopPanel, forceStopContext,
  blockedActions: { showRetry, showResume, showRerun, showStop, isInterrupted, showProjectedCheckRepair },
  showStandaloneRerun, showError, errorMessages, showOtherAgents, otherAgentsCount,
}
```

`IssueActionsCard` becomes a thin renderer over this shape. Rationale: the branching *is* the complexity (it drives the 211 score); lifting it out as a pure function makes it unit-testable in isolation (precedent: `epic-detail/model/primaryLifecycleAction.ts`) and shrinks the measured complexity of both the card and the page.

**Alternative considered:** keep the branching inline and only relocate the JSX into `IssueActionsCard`. Rejected — it moves code without reducing complexity or improving testability, which is the actual goal of the epic. The pure-derivation approach is what lets the card be a dumb renderer.

The `showCheckRepairActions = false` constant and the `canStopWorkflow` expression (334–338) are preserved verbatim inside `computeActionsState`.

### D4 — `useConfirmOutsideClick()` absorbs both duplicated effects

Single hook in `model/`:

```ts
function useConfirmOutsideClick(opts: {
  confirming: boolean, setConfirming: (v: boolean) => void, timeoutMs?: number
}): React.RefObject<HTMLDivElement>
```

It owns the `setTimeout(5000)` + `mousedown` listener (the union of lines 222–235 and 319–332) and returns a ref. Two call sites in `IssueActionsCard` (force-stop panel, stop panel) replace the page-level `forceStopPanelRef` / `stopPanelRef` + two `useEffect`s. The refs move with the card so they stay co-located with their only consumers, as the proposal requires.

### D5 — `useIssueDetailMutations()` owns query-invalidation; UI state stays local

The hook centralizes the 13 `useMutation` definitions and their `onSuccess` **query invalidation** only. The mutations that also mutate *local* UI state on success (`forceStop`/`stop` reset confirming, `addComment` clears the field, `deleteComment` clears `deletingCommentId`/`deleteCommentError`) take an optional `onSuccess` callback in the hook's options, so the local-state reset is wired **by the owning component** rather than hidden in a shared hook. This keeps the hook free of UI concerns while preserving exact behavior.

**Alternative considered:** return `mutateAsync` and let components `.then()` the reset. Rejected — the existing code resets inside `onSuccess`/`onError` (incl. error state on `deleteComment`), and mirroring that with callbacks is a smaller, more faithful diff.

### D6 — Shared helpers move to `model/format.ts`

`formatRelativeTime` is consumed by both `IssueCommitsSection` and the force-stop panel; `attachmentFromMetadata` by both `IssueDescriptionSection` and `IssueCommentsSection`; `formatStageName`/`stageToIssueStatus`/`WORKFLOW_STAGE_LABELS` by the pills and `IssueDetailsCard`. Centralizing them in `model/format.ts` avoids duplicate imports across the new files.

`resolveIssueAttachment` is a **closure** built in the page from `issueNumber` / `issueProjectId` / `issue.attachments`; the page keeps building it and passes it as a prop to the description and comments sections (unchanged behavior).

## Risks / Trade-offs

- **[Density test breaks if a structural wrapper's owner changes]** → Mitigation: D2 — structural `mb-8` / `space-y-*` / `gap-*` elements and their testids stay in the page; children render inner content only. Verify by running the issue-180 density block after each section extraction, not just at the end.
- **[Prop-drilling increases surface area]** → Sections/cards need many props (`issue`, `agentStatus`, `workflowTimeline`, `diffData`, `commitsData`, mutations, `navigate`). → Trade-off accepted: explicit props over hidden coupling. The props are the real data-dependency graph, which is the point of the decomposition. No context provider introduced (would add machinery for a single page).
- **[State-machine extraction could subtly reorder render output]** → The Actions card uses one IIFE (`(() => {…})()` at 1170) and several sibling conditionals. → Mitigation: `computeActionsState` returns a *description* of what to show; the card preserves the **exact** JSX order/structure from the original. The new `actionsState.test.ts` asserts the derivation; the existing `IssueDetailPage.test.tsx` Actions cases assert the rendered output.
- **[Mutation `onSuccess` split across hook + component]** → Slight indirection. → Trade-off accepted for testability; documented at the hook's call site.
- **[46 `data-testid`s must all survive]** → Mitigation: grep-driven checklist — before/after `rg "data-testid" IssueDetailPage.tsx` count must stay 0 for moved testids and the page-wide union must be unchanged; the `.test.tsx` suites (which assert most of them) are the hard guard.

## Migration Plan

No schema, API, config, or deploy step — this is source-only. Recommended commit sequence (each commit leaves the 4 suites + `npm run typecheck -w packages/web` green):

1. Extract helpers + pills + `WorkflowYamlDialog` (mechanical move; no page-body change).
2. Add `model/useConfirmOutsideClick.ts` (+ unit test); swap both `useEffect`s.
3. Add `model/useIssueDetailMutations.ts`; rewire the page to it.
4. Add `model/actionsState.ts` (+ unit test); build `cards/IssueActionsCard.tsx` consuming it + the click-outside hook.
5. Extract the remaining cards (Details/Drift/Configuration/Prerequisites/Readiness) and the four sections.
6. Final: confirm `IssueDetailPage.tsx` is orchestration-only; run all 4 suites + typecheck + lint.

**Rollback:** revert the branch/PR. No data or runtime state to recover.

## Open Questions

- **Pills granularity** — confirm one `pills.tsx` (five exports) is acceptable vs. one-file-per-chip. Recommendation: one file; revisit if a reviewer wants per-file.
- **`actionsState` return shape** — the exact field set will firm up during D3 implementation; the test file should pin the cases that the existing Actions tests already cover (archived note, start variants, force-stop panel visibility, blocked retry/resume/rerun/stop, standalone rerun) so the contract is explicit.
- **Whether `useIssueDetailMutations` belongs in `model/` or `ui/`** — it calls React Query, not pure logic. Placing it in `model/` matches `epic-detail` (which also holds hook-bearing files there); placing it in `ui/` would be defensible. Default: `model/` for consistency.
