# Design: Start issues directly from the epic page (inline start)

## Context

The epic surfaces already compute and display the next startable issue and each linked issue's startability, but they are display-only. Starting an issue requires navigating into the issue detail page. This change adds a Start write-action onto two existing epic read surfaces:

- **Epic list card** next-issue area (`packages/web/src/pages/epics/ui/EpicListPage.tsx`, `EpicCard` / `statusText`).
- **Epic detail linked issue row** (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`, `LinkedIssueRow`).

Current state that this change builds on (all already shipped by prerequisite #171):

- Server `LinkedIssueDto` carries `CanStart` and `StartBlocker`, populated in `EpicQuerier.GetLinkedIssuesAsync` from `IssueInfo.CanStart` / `Blocker`.
- `EpicProgressDto.NextIssue` / `NextIssueReason` carry the projected next-issue signal. Per `epic-tracking`, `nextIssue` is only ever the highest-priority linked issue whose derived readiness reports `CanStart` with no `Blocker` — so `nextIssue != null` is equivalent to "startable".
- `issue-start-readiness` fully defines `canStart` / `Blocker` derivation; this change consumes it unchanged.

Constraints / stakeholders:

- Start is an issue-aggregate write (`IssueGrain.StartWorkAsync`, `POST /issues/{n}/start`). The epic surface only consumes the result and the gating signal; it must not touch the issue state machine itself.
- Two query caches cross on success: epic (`['epics', …]`) and issue (`['issues', …]`). An SSE-driven `LiveTaskProvider` also invalidates these keys, so invalidations must be idempotent.
- The epic list card is one big click target (card click → navigate to epic detail). A Start button inside it must not trigger navigation.

See `proposal.md` for motivation and `specs/epic-inline-start/spec.md` for normative requirements.

## Goals / Non-Goals

**Goals:**

- Expose a Start action on the two epic surfaces, gated on the read model's `canStart` / `nextIssue` with zero client-side recomputation of readiness.
- Reuse the existing issue start path with no new endpoint, no batch start, no change to start semantics.
- On success, refresh epic list + epic detail + issue caches so the started issue shows `in_progress` and the epic's progress / next-issue / current-activity all update from one source of truth.
- On failure, surface a toast and leave cached state intact (no optimistic advance).

**Non-Goals:**

- No batch start, no dependency-graph start node, no start/approval/workflow semantics change.
- No server change (DTO fields already present from #171).
- No recomputation of `canStart` / `Blocker` on the client.
- No confirmation modal for starting (low-risk, fire-and-forget per proposal).

## Decisions

### Decision 1: Add a single `useStartIssue` hook co-located in the epic entity layer

Both consuming surfaces are epic pages and share the same invalidation contract (epic + issue). Add `useStartIssue` in `packages/web/src/entities/epic/api/queries.ts` alongside the other epic mutations:

```ts
export function useStartIssue() {
  const qc = useQueryClient()
  const { projectId } = useProject()
  return useMutation({
    mutationFn: (number: number) => startIssue(number, projectId),   // reuse entities/issue/api/client.ts
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['epics'] })   // covers list ['epics', projectId] and detail ['epics', projectId, id] by prefix
      qc.invalidateQueries({ queryKey: ['issues'] })  // covers issue list/detail/kanban by prefix
      toast.success('Issue started')
    },
    onError: (err: Error) => { toast.error(err.message || 'Failed to start issue') },
  })
}
```

This mirrors the existing `useAddEpicIssue` / `useRemoveEpicIssue` invalidation pattern exactly (`['epics']` + `['epics', epicId]` collapse into the single `['epics']` prefix here because Start is not scoped to one epic in the URL — the refetch of list + the currently-open detail both match the prefix).

- **Alternative A: shared `useStartIssue` in the issue entity layer.** Rejected — the invalidation scope is view-specific. An issue-page start invalidates `['issues']`; an epic-page start must additionally invalidate `['epics']`. Putting the hook in the issue layer would force every call site to add extra invalidation, duplicating the contract the spec defines once.
- **Alternative B: inline `useMutation` in each epic page.** Rejected — duplicates the invalidation + toast logic across `EpicListPage` and `EpicDetailPage`; the spec defines one contract.

The operation itself still belongs to the issue domain — we reuse the `startIssue` API client unchanged. Only the cache-invalidation shell lives in the epic entity.

### Decision 2: One pure gating predicate, shared by card and row

Add a pure helper in `packages/web/src/entities/epic/model/` (e.g. `inline-start.ts`):

- `canInlineStartRow(issue: LinkedIssue): boolean` → `issue.canStart && !isInFlightOrTerminal(issue)`, where in-flight/terminal = `status === 'in_progress' | 'done' | 'cancelled'` OR `health === 'blocked'`.
- Card gating needs no predicate: it is purely `progress.nextIssue != null` (already startable by the `epic-tracking` definition of `nextIssue`).

Rationale: the two surfaces read different shapes (card reads `EpicProgress.nextIssue`; row reads `LinkedIssue.canStart`), but both reduce to "consume the read model, do not recompute". Centralising the row predicate keeps the terminal/in-flight exclusion testable in isolation and identical across any future surface.

### Decision 3: Extend the web `LinkedIssue` type with `canStart` and `blocker`

`request<EpicDetail>(…)` returns server JSON as-typed, so this is a type-only change in `packages/web/src/entities/epic/model/types.ts`:

```ts
export interface LinkedIssue {
  …
  canStart: boolean
  blocker: IssueStartBlocker | null   // reuse the type already used by Issue at entities/issue/model/issue.ts
}
```

The server already emits these via the shared `IssueStartBlockerDto` polymorphic serializer (`$type` discriminator `draft` / `waiting-for`). Because the issue detail page already consumes `issue.blocker` successfully off the same DTO, the discriminator wire-format is already known-good; we reuse the existing `IssueStartBlocker` TS shape rather than redefining it.

### Decision 4: Start button stops card navigation; pending state disables it

On the epic list card, the whole `<Card onClick={navigate}>` is a navigation target. The Start `<Button>` renders inside the next-issue area and calls `e.stopPropagation()` in its `onClick` so starting does not also navigate into the epic. While a start is in flight, the button is disabled via the mutation's `isPending` to prevent double-start races (the server would refuse a second active run anyway, but disabling gives tighter UX).

- **Alternative: remove the card-level click handler and add an explicit "Open" affordance.** Rejected — out of scope, larger UX change, breaks the existing `epic-board` card interaction.

### Decision 5: No optimistic update; rely on invalidation + refetch

`onSuccess` invalidates and lets TanStack refetch. This is simpler and correct over hand-rolled optimistic patches across two caches, and it matches every other epic mutation in the file. The spec explicitly forbids optimistically advancing to `in_progress` on failure; invalidate-on-success satisfies that trivially because the cache only changes after the server confirms.

## Risks / Trade-offs

- **[Start button inside a navigable card triggers navigation]** → `e.stopPropagation()` on the button `onClick`; covered by an `EpicListPage` test that asserts start fires and navigation does not.
- **[Polymorphic `blocker` discriminator mismatch between `LinkedIssueDto` and the TS `IssueStartBlocker` shape]** → reuse the exact TS type the issue detail page already consumes; the DTO is the same `IssueStartBlockerDto`. Verify with a detail-page fixture that renders a `waiting-for` row.
- **[Double-click / concurrent start race]** → button disabled while `isPending`; server-side `IssueGrain.StartWorkAsync` active-run refusal is the authoritative guard regardless.
- **[SSE invalidation racing the mutation invalidation]** → both invalidate the same prefix keys; TanStack dedups in-flight refetches. Idempotent, no conflict.
- **[Stale gating after start]** → after success, invalidation refetches epic detail; the started issue now has `status === 'in_progress'` so `canInlineStartRow` returns false and the row hides Start on its own. No extra state to manage.

## Migration Plan

This is a frontend-only change — no server deploy, no DB migration, no API contract change (the relevant DTO fields shipped with #171).

- **Deploy:** ship the web bundle. The new `LinkedIssue` fields are additive; older clients that ignore them are unaffected.
- **Rollback:** revert the web changes; no server-side coupling to undo.
- **Verification:** `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; add/extend cases in `EpicListPage.test.tsx` (Start visibility by `nextIssue`/`nextIssueReason`, invocation + no-navigation, failure toast) and `EpicDetailPage.test.tsx` (`LinkedIssueRow` gating across `canStart` × in-flight/terminal/blocked, Start invokes the start mutation, Remove/navigation unchanged).

## Open Questions

- Success toast copy — "Issue started" matches the brevity of the existing epic toasts; confirm or adjust during implementation. (Failure toast reuses the server message verbatim, consistent with `useAddEpicIssue`.)
- Whether the list-card Start should also appear when an epic already has an in-flight issue (the card shows both "In progress" and "Next" today). Current design: yes — the two lines are independent and `nextIssue != null` alone gates the button. Revisit if user testing shows it clutters the card.
