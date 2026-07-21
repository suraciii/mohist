## Context

Issue #457 bundles nine independently verifiable presentation defects on the Web issue detail page (`packages/web/src/pages/issue-detail` and the `widgets/issue-workflow` blocks it renders). Each is a localized invariant violation — no contract, persistence, routing, server, runner, or CLI surface is involved. The current state, per defect:

1. **Copy leaks** — `BranchBar.tsx:129` renders `未能检查上游`; `PrDeliveryIndicator.tsx:37` renders `经由 PR #N 合并`. The rest of the page is English.
2. **Dark-theme breakage** — `BranchBar.tsx` and the workflow status/task presentation (e.g. `WorkflowRunStatusPill.tsx`, `TaskLogPanel.tsx`) use literal palette utilities (`amber/blue/red/green/gray-*`) that ignore the dark theme. The design system already defines semantic token families in `app/styles/index.css`: `success / warning / info / danger`, each with `-subtle / -border / -foreground`, plus `muted / muted-foreground / border / background`.
3. **Component inconsistency** — `WorkflowSessionsPanel.tsx` (`SessionFilterControls`) renders three native `<select>` elements, while the rest of the app uses the shared base-ui `Select` (`shared/ui/components/select.tsx`; usage pattern in `pages/issue-changed-files`).
4. **Wrong label** — `IssueDetailsCard.tsx:56` and `:71` both render `<dt>Parent Issue</dt>`; the second row describes children.
5. **Truncated header** — `CollapsibleRailCard.tsx:59` applies `truncate` to the title, cutting "Configuration" to "CONF…" at desktop rail width.
6. **False session copy** — `WorkflowSessionsPanel.tsx:66` returns `'No usage yet'` whenever token figures are absent, even for sessions that ran and produced artifacts.
7. **Missing states** — `IssueDetailPage.tsx:218-228` renders a bare `Loading...` line while loading, and maps *every* `isError` to `NotFoundState`, so a transient fetch error is indistinguishable from a real 404. `useIssue` (TanStack Query) yields an `ApiError` carrying `status` (`shared/api/client.ts`), so 404 is distinguishable from transient failure. A `Skeleton` primitive already exists.
8. **Rail scrolls away** — `IssueDetailPage.tsx:553` reference-rail column has no sticky positioning; the page scroll container is the outer `overflow-y-auto` div (`:287`).
9. **Invisible disabled** — `button.tsx` expresses disabled only as `disabled:opacity-50`; a disabled primary button (e.g. the empty-comment submit in `IssueCommentsSection.tsx:151`) still reads as active.

See `proposal.md` for motivation and `specs/<capability>/spec.md` for normative requirements. This document covers the **how**.

## Goals / Non-Goals

**Goals:**
- Restore each of the nine invariants with the smallest localized change per defect.
- Route every color decision through existing semantic theme tokens so light/dark just work.
- Reuse existing shared primitives (`Select`, `Skeleton`, `Button`) rather than inventing new surfaces.
- Keep the change Web-only and contract-free.

**Non-Goals:**
- Decision-surface disabled honesty and copy (sibling decision-surface issue).
- Section restructuring / deduplication (sibling reading-flow issue).
- Defects on pages other than issue detail.
- Introducing an i18n framework, new CSS token families, server/runner/CLI/API/persistence changes, or feature flags.

## Decisions

### D1 — Copy fixes are inline English literals; no i18n layer
- `PrDeliveryIndicator.tsx:37` → `Merged via PR #{prNumber}`.
- `BranchBar.tsx:129` → `Upstream check unavailable` (wording aligned with the existing reason copy "Branch status could not be checked.").
- Alternative considered: extract strings into a copy module. Rejected — the app uses inline English literals everywhere and has no i18n infrastructure; a module would be inconsistent overhead.

### D2 — Truthful session usage copy: replace the false placeholder, not the data path
- `usageText` (`WorkflowSessionsPanel.tsx:59-67`): when token figures are absent, return `Usage unavailable` instead of `No usage yet`. This is truthful regardless of why the figures are missing, and matches the spec example. The row continues to surface the metrics that *are* known (status, tool-call count, context %, cost, time).
- Alternative considered: conditionally omit the usage cell. Rejected — an explicit truthful label reads more clearly than a silently missing metric, and the spec requires copy "consistent with what is known."

### D3 — Map literal palette to existing semantic token families
No new tokens. Per-state mapping for `BranchBar.tsx` (and the same families for the workflow status/task blocks):
- amber (behind / rebase-available) → `bg-warning-subtle border-warning-border`, label `text-warning`.
- blue (rebasing / in-progress) → `bg-info-subtle border-info-border`, label `text-info`.
- gray (upstream unknown; disabled rebase) → `bg-muted border-border`, label `text-muted-foreground`.
- red (errors / conflicts) → `bg-danger-subtle border-danger-border`, label `text-danger`.
- green (up to date) → `text-success`.
- The `enabledClassName` / `reasonClassName` strings passed into `RebaseAction` become these token strings. `WorkflowRunStatusPill.tsx` and the task/log presentation map `running→info`, `blocked/waiting→warning`, `failed→danger`, `done→success`, `idle→muted`.
- Alternative considered: introduce a dedicated `status-*` token layer. Rejected — `success/warning/info/danger` already cover the states and already have dark variants; another layer adds CSS surface for no gain.

### D4 — Migrate the three session filters to the shared base-ui `Select`
- Replace each native `<select>` in `SessionFilterControls` with `<Select value onValueChange>` + `SelectTrigger size="sm"` + `SelectContent`/`SelectItem`, mirroring the `pages/issue-changed-files` toolbar. Reuse the existing `STATUS_LABELS / STAGE_LABELS / SORT_LABELS` maps.
- Nullable "All" option: model `null` as an empty-string value — `value={filter ?? ''}` with a leading `<SelectItem value="">All …</SelectItem>`, and `onValueChange={v => onChange(v === '' ? null : v)}`. Preserve the existing `data-testid` hooks (move onto the trigger) so tests stay stable.
- Alternative considered: keep native selects and restyle them. Rejected — the invariant is "shared component everywhere," and base-ui Select already matches the app.
- To verify during implementation: confirm base-ui accepts `value=""` as a selectable item; if it rejects empty values, fall back to a `__all__` sentinel mapped back to `null` at the boundary.

### D5 — Relabel the child-issues row distinctly
- `IssueDetailsCard.tsx:71`: change the duplicate `<dt>Parent Issue</dt>` to `<dt>Parent of</dt>` (the row value is "is a parent (N child issues)"). This is distinct from the real parent-reference label at `:56` ("Parent Issue") and from the children-progress row at `:79` ("Children").
- Alternative considered: merge the two child rows into one. Rejected — out of scope (reading-flow restructuring belongs to the sibling issue).

### D6 — Stop truncating rail headers; make the desktop rail sticky
- Header: in `CollapsibleRailCard.tsx:59`, drop `truncate` from the title span (use `break-words`/`text-balance`, keep `flex-1`). The collapsed `summary` span keeps its own `truncate` — summaries may still elide, titles must not.
- Sticky rail: in `IssueDetailPage.tsx:553`, on the reference-rail column add desktop-only sticky sizing: `lg:sticky lg:top-6 lg:self-start lg:max-h-[calc(100vh-3rem)] lg:overflow-y-auto`. The outer `overflow-y-auto` container (`:287`) is the scroll ancestor, so `lg:sticky` keeps the rail in view; `max-h` + internal overflow prevents an over-tall rail from being unreadable. Narrow viewport is untouched (it stacks/collapses as today).
- Alternative considered: JS scroll-spy / `position: fixed`. Rejected — CSS sticky needs no JS and respects layout flow.

### D7 — Per-section skeletons + split 404 from transient error
- Loading: replace the bare `Loading...` block (`IssueDetailPage.tsx:222-228`) with a small `IssueDetailPageSkeleton` that mirrors page structure (status header, reading-flow blocks, rail cards) using the existing `Skeleton` primitive. Sub-section queries that already degrade (diff/commits/sessions) keep their own handling; the page-level shell is the visible fix.
- Error branching: in the `isError` path, classify via `error instanceof ApiError && error.status === 404` → render `NotFoundState`; otherwise render a new lightweight `ErrorState` with a Retry button wired to `refetch()`. Add `ErrorState` to `shared/ui` (semantic tokens, `text-danger`, a `Button variant="outline"` retry). `NotFoundState` is reused unchanged for the 404 case (its own literal `gray/blue` palette is explicitly out of scope per Non-Goals).
- Alternative considered: retry on every error including 404. Rejected — a 404 will not resolve on retry and the two states must be visually distinct (spec requirement).

### D8 — Strengthen disabled affordance in the shared `Button`
- In `button.tsx` base cva, replace the disabled treatment with an unmistakable neutral state: `disabled:pointer-events-none disabled:cursor-not-allowed disabled:bg-muted disabled:text-muted-foreground`. The `:disabled` pseudo-class outweighs variant bg/text classes by specificity, so every variant collapses to the same inert neutral when disabled.
- Verification focuses on the issue-detail buttons in scope (empty-comment submit `IssueCommentsSection.tsx:151`, in-flight delete `:102`), but the fix lands in the shared primitive so the invariant holds app-wide.
- Alternative considered: per-button disabled overrides on issue detail only. Rejected — it leaves other disabled buttons inconsistent, is more code, and "disabled reads as disabled" is a component-level concern.

## Risks / Trade-offs

- [Existing tests assert literal palette classes] → `BranchBar.test.tsx` asserts `border-amber-300 text-amber-800 hover:bg-amber-50`, and `WorkflowRunStatusPill.test.tsx` asserts `bg-blue-100`. These encode the old behavior and **must** be updated to the new token classes as part of the change — expected, not a regression.
- [Shared `Button` disabled change is app-wide] → It only alters the `:disabled` state (no change to enabled buttons). Mitigate with `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and a visual spot-check of disabled buttons in light/dark.
- [base-ui `Select` empty-value "All" option] → If base-ui rejects `value=""`, use a `__all__` sentinel mapped to `null` at the boundary. Keep `data-testid` stable so `WorkflowSessionsPanel.test.tsx` needs only minimal updates.
- [Sticky rail on very tall rail content] → Capped with `lg:max-h` + internal `overflow-y-auto`; only applied at the `lg` breakpoint so narrow viewports are unaffected.
- [Dark-theme contrast after token swap] → Every chosen token already has a `.dark` variant in `index.css`; verify each replaced block in dark mode (branch bar states, status pill, task/log presentation).
- [Scoped copy sweep] → Grep the issue-detail widgets for CJK to catch any unlisted leaks, but do not expand to other pages (Non-Goal).

## Migration Plan

- Web-only change; no server, runner, CLI, API, persistence, or routing contract changes, no database migration, no feature flag.
- Lands as one changeset covering the seven capability areas. No phased rollout — the defects are presentational and independently verifiable.
- Verification: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` must pass; manual light/dark + desktop/narrow pass over the issue detail page confirming each acceptance criterion.
- Rollback: revert the Web commits; no data or external-state cleanup required.

## Open Questions

- Final English wording for the two copy fixes (proposed: `Merged via PR #N`, `Upstream check unavailable`) — confirm in the plan stage.
- Whether `NotFoundState`'s own literal `gray/blue` palette should be tokenized opportunistically — default **no** (out of scope); revisit only if it blocks the dark-theme acceptance criterion.
- base-ui `Select` behavior with an empty-string value — confirm during D4 implementation and apply the sentinel fallback if needed.
