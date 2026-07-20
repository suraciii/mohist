# Self-Review — Issue #430 Session Page Frame Consolidation (after feedback fb_84ba3c6d)

Scope: `openspec/changes/issue-430/{proposal.md, design.md, tasks.json, specs/}` against the feedback directive (Occam's razor; address every D-1..D-11 finding; mutual consistency across proposal/specs/design/tasks) and the issue body.

## Re-checked findings against D-1..D-11

| id | finding | resolution (line refs in this rev) |
|---|---|---|
| D-1 | `StickySessionTitle`'s `engaged` default contradicts the spec's "hidden on first render" invariant | Fixed via Occam — the wrapper owns visibility, the inner `StickySessionTitle` has no new prop at all. Wrapper starts at `engaged=false` and renders `null` until the IntersectionObserver's first callback reports the header fully out. See `design.md:62` (D2 description + alternatives) and `tasks.json` T-003 acceptance criterion 1. The `engaged=true` default option is explicitly rejected at `design.md:73`. |
| D-2 | `formatSessionTime` signature had a redundant `anchor` parameter | Fixed — signature is now `formatSessionTime({ date, statusKind, now })`. Helper takes only one timestamp. `design.md:42`, `design.md:58` (rejected alternative), `tasks.json` T-001 description + acceptance criterion 1. The spec's "anchor timestamp" language now maps unambiguously to the `date` parameter. |
| D-3 | `prereq` / `unknown` disabled-reason triggers had no input → reason mapping | Fixed via Occam — dropped the closed-set entirely. The only currently-known disabled trigger is `data-active="true"`, which `SessionRecoveryActions.tsx:195, 211` already exposes. The structured tooltip is keyed off that attribute; no new contract attribute is introduced. `design.md:122` (D6 description), `design.md:139` (rejected alternative), `specs/session-action-weight/spec.md` (rewritten — closed `prereq`/`unknown` scenarios removed, single "active" scenario retained), `tasks.json` T-005 description + acceptance criteria 1–3. Future reasons are deferred in `design.md:215`. |
| D-4 | `session-cancel-trigger` testid preservation not pinned in T-002 or T-004 | Fixed — acceptance criterion 5 in T-002 fences the button out of T-002's scope ("MUST NOT alter the Cancel button's location, variant, or testid"), and acceptance criterion 1 in T-004 explicitly preserves `data-testid="session-cancel-trigger"` and `aria-label="Cancel session"` across the demotion. `tasks.json` T-002 acceptance 5 + T-004 acceptance 1. |
| D-5 | Sidebar conditional rendering implied but not explicit | Resolved via Occam in the opposite direction — the sidebar is **not** conditionally rendered (no new render gate); it stays CSS-driven via the parent's existing `xl:flex-row` layout. The spec's `is not rendered` language is updated to `is not visible (CSS-hidden)` in `specs/session-sibling-nav-dedup/spec.md`. T-006 acceptance criteria 2 and 3 assert visibility via the parent layout, not via data-testid absence. `design.md:140` (D7 description), `design.md:151` (rejected alternative), `tasks.json` T-006. |
| D-6 | Disabled Compact/Reset focusability was undefined | Fixed — the existing `Tooltip` primitive (`packages/web/src/shared/ui/components/tooltip.tsx`) already wraps its child in a `tabIndex={0}` span with `onFocus`/`onBlur` toggling the tooltip; that primitive IS the focus mechanism. No new a11y code is added. `design.md:122` (D6 description), `tasks.json` T-005 acceptance criterion 3 makes this explicit. The Risk bullet is updated from a hedge to a confirming statement at `design.md:179`. |
| D-7 | T-001 description referenced a non-existent sticky-strip time display | Fixed — "(when its time display lands)" parenthetical removed from `tasks.json` T-001 description. The call sites in scope are the header's `lastActivityAt` and probing indicator only. |
| D-8 | T-002 didn't lock "leave Cancel alone" | Fixed — T-002 acceptance criterion 5 explicitly forbids modifying the Cancel button's location, variant, or testid; that work is fenced to T-004. |
| D-9 | "all six branches of the matrix" wording | Clarified to "all six matrix rows" in `tasks.json` T-001 acceptance criterion 3. |
| D-10 | Anchor timestamp (`lastActivityAt` vs `completedAt`) not pinned | Fixed — `tasks.json` T-001 acceptance criterion 2 pins `date = max(completedAt, lastActivityAt)` for terminal sessions, matching `specs/session-time-display/spec.md` requirement 2. |
| D-11 | Existing Sent-flash test backward-compat not asserted in T-007 | Fixed — `tasks.json` T-007 acceptance criterion 5 explicitly asserts the existing `visibility and enabled state`, `disabled (terminal state)` (with `/no longer accepting followups/i` text), and the transient `Sent` flash spec continue to pass unchanged when no new props are supplied. |

## Mutual consistency check

| invariant | proposal | spec | design | tasks | status |
|---|---|---|---|---|---|
| `formatSessionTime({ date, statusKind, now })` only one timestamp | `proposal.md` references the helper by name (post-fix) | `session-time-display/spec.md` uses "anchor timestamp" as a domain term mapping to `date` | `design.md:42, 58` | `tasks.json` T-001 acceptance 1 | consistent |
| `StickySessionTitle` inner component has no new prop | `proposal.md:10` says sticky is hidden until scroll | `session-sticky-identity/spec.md` requires hidden on first render | `design.md:62-65, 73` (wrapper owns state) | `tasks.json` T-003 acceptance 2 | consistent |
| No `data-disabled-reason` closed-set | `proposal.md:12, 40, 52` explicitly drop the closed-set rationale | `session-action-weight/spec.md` rewritten — single `active`-driven tooltip, no closed-set | `design.md:122, 133, 139, 24, 194` | `tasks.json` T-005 acceptance 1-3 | consistent |
| Sidebar stays CSS-hidden (no new render gate) | implicit — proposal preserves sidebar | `session-sibling-nav-dedup/spec.md` updated to "is not visible" | `design.md:140-151` | `tasks.json` T-006 acceptance 2-4 | consistent |
| `session-cancel-trigger` preserved | implicit — Cancel demoted but kept | unchanged | `design.md:114` keeps the same button id by reference | `tasks.json` T-002 acceptance 5 + T-004 acceptance 1 | consistent |
| Anchor is `max(completedAt, lastActivityAt)` | implicit | `session-time-display/spec.md:22` defines | `design.md:42` notes | `tasks.json` T-001 acceptance 2 pins | consistent |
| Sent-flash + closed-copy backward-compat | implicit | `session-followup-state-hints/spec.md:67` requires it | `design.md:159` discusses | `tasks.json` T-007 acceptance 5 pins | consistent |

## Capability coverage verification

All six capabilities in the proposal's Capabilities section still have a spec file, a design decision, a task, and ≥1 `#### Scenario:`

- `session-header-meta-line` — spec `reqs 4 / scenarios 6`; design D3 + D4; task T-002.
- `session-sticky-identity` — spec `reqs 4 / scenarios 6`; design D2; task T-003.
- `session-action-weight` — spec `reqs 3 / scenarios 7` (was 9 — two scenarios trimmed per Occam as part of D-3); design D5 + D6; tasks T-004 + T-005.
- `session-sibling-nav-dedup` — spec `reqs 3 / scenarios 5`; design D7; task T-006.
- `session-time-display` — spec `reqs 4 / scenarios 9`; design D1; task T-001.
- `session-followup-state-hints` — spec `reqs 6 / scenarios 11`; design D8; task T-007.

Net change since the previous review: 2 spec scenarios removed (the speculative `prereq` / `unknown` cases); all other counts unchanged. Every remaining scenario is testable; every acceptance criterion in `tasks.json` includes test-related verification.

## What the implementation will have to do (build-side)

Because this is the plan stage, there is no code to lint / test yet — the artifacts above are what the build stage will execute against. The smallest verification command the build stage will run is `npm run typecheck -w packages/web && npm run test:run -w packages/web`, which is explicit in every task's terminal acceptance criterion. The plan stage itself was verified by:

- `node -e "require('./openspec/changes/issue-430/tasks.json')"` succeeds (JSON valid).
- `node -e ... deps DAG check` passes (every `dependsOn` references a strictly lower priority; no cycles).
- Cross-grep for `data-disabled-reason` returns only explanatory rationale mentions (proposal/design/spec/tasks all explaining WHY the closed-set was dropped), no stale contract assertions.
- Cross-grep for `formatSessionTime` signature returns the consistent `{ date, statusKind, now }` form across design + tasks.
- Cross-grep for `StickySessionTitle` confirms the inner component has no new prop; engagement is owned by the wrapper.

## Verdict

All D-1..D-11 findings are resolved in the current revision of proposal / design / specs / tasks, with mutual consistency preserved. The feedback directive ("Apply Occam's razor: remove redundant or undefined states and parameters instead of adding mechanisms") is satisfied: the helper's `anchor` parameter is dropped, the `data-disabled-reason` closed-set is dropped, the sticky `engaged` prop on the inner component is dropped (the wrapper owns visibility instead), and the sidebar's conditional-render gate is dropped (CSS-hidden is sufficient). The remaining artifacts describe the minimal mechanism set that satisfies the issue's acceptance criteria and stay within the epic-#49 boundary (no data / protocol / liveness-gate / row-anchor / live-activity / jump-highlight changes).

<promise>PASS</promise>
