# Self-Review — Issue #430 Session Page Frame Consolidation

Scope reviewed: `openspec/changes/issue-430/{proposal.md, design.md, tasks.json, specs/}` against the issue body (`mo issue show 430`) and the epic-#49 / issue-#427/#428/#429 boundary.

## What is solid

- **Capability coverage is complete.** All six product shapes in the issue map to a spec file, a design decision (or two), and a task. No capability is left without a normative spec, and every requirement has at least one `#### Scenario:` (sessions-header-meta-line: 4/6, session-sticky-identity: 4/6, session-action-weight: 3/9, session-sibling-nav-dedup: 3/5, session-time-display: 4/9, session-followup-state-hints: 6/11). Specs use SHALL/MUST and start directly with `### Requirement:` blocks (no `## ADDED/MODIFIED/REMOVED` headers), satisfying the spec format rules.
- **Epic-#49 boundary is respected.** Design non-goals explicitly exclude transcript content (#427), row anchors, liveness gate (#426), live activity (#428), jump highlight (#429), sidebar content redesign, and application-wide navigation. Data model, event protocol, and `SessionDataSourceResult` contract are all pinned as untouched.
- **Time-injection discipline is correctly internalized.** D1's `formatSessionTime` consumes `now` as an argument; T-001 acceptance criterion 4 explicitly forbids implicit `Date.now()`. The followup closed-state copy in T-007 reuses the same helper for phrasing consistency, matching spec session-followup-state-hints "the helper that produces the relative phrase MUST be the same helper used elsewhere on the page".
- **Task DAG is valid.** `node` validation confirms acyclic deps pointing only to lower-priority IDs; T-002 and T-007 correctly depend on T-001; T-003/T-004/T-005/T-006 are independent and parallelizable. Every task is one feature module and includes its own acceptance criteria with test-related verification.
- **Each capability closes with a "presentational only" requirement** (or equivalent in session-time-display via the "Time-format helper is deterministic under test" + "Underlying data fields are unchanged" rules), giving the implementer a clear invariant not to drift into data/protocol changes.

## Critical defect (must fix)

### D-1: `StickySessionTitle`'s `engaged` default contradicts the spec

- **Where:** `design.md:63` (D2) — "a new `engaged` boolean prop (default `true` for callers that don't yet pass the new prop — backward compatibility for the existing widget public API)".
- **Conflict:** `specs/session-sticky-identity/spec.md:1–3` requires "the sticky strip SHALL NOT occupy any visible space while the outer session header is fully or partially visible" and "the outer header is the single source of identity (session name + status) on the first screen". A default of `engaged=true` would render the strip visible on first render, directly violating the spec.
- **Fix:** the default for the wrapper's resolved `engaged` state MUST be `false`. The "backward compatibility for the existing widget public API" rationale no longer applies — the widget's behavior is changing. The wrapper owns the initial `engaged=false` and the IntersectionObserver callback is the only thing that flips it to `true`. If a prop is exposed on `StickySessionTitle` for tests, its default must be `false`.
- **Impact if not fixed:** T-003 will be implemented one of two ways — either the strip is initially visible (failing the first render scenario in `session-sticky-identity`) or the engineer guesses at the correct default and ships something not specifiable.

## Major defects (must fix before build)

### D-2: `formatSessionTime` signature carries a redundant/unclear `anchor` parameter

- **Where:** `design.md:42` (D1) — "the new helper is `formatSessionTime({ date, statusKind, anchor, now })`".
- **Conflict:** the spec (`session-time-display`) talks about a single timestamp (the anchor for the threshold and the time being formatted are the same input). The matrix's `now - anchor ≥ 1h` column header is threshold semantics, not a separate parameter. An extra `anchor` parameter is either redundant with `date` or unclear about which one is which.
- **Fix:** reduce the signature to `formatSessionTime({ date, statusKind, now })`. The helper computes the threshold internally as `now - date`. T-001's acceptance criterion 2 must match this signature.
- **Impact if not fixed:** implementation diverges between design and tests; engineers will invent semantics.

### D-3: `prereq` and `unknown` disabled-reason triggers are not pinned down

- **Where:** `specs/session-action-weight/spec.md:40–48`, `design.md:122–129` (D6), `tasks.json` T-005.
- **Conflict:** the current `SessionRecoveryActions.tsx:103` derives `active` only via `recoveryAvailable === undefined ? isSessionActive(status) : !recoveryAvailable`. The spec scenarios describe `prereq` and `unknown` with examples ("prerequisite data is missing", "network or status query is pending"), but no decision rule maps an input state to either reason. The design D6 table lists the three reasons but does not say *when* each fires. T-005 acceptance criterion 1 only requires the closed-set attribute to exist, with no input→reason rule.
- **Fix:** add a trigger table (either to D6 or as a follow-on clarification) covering at minimum:
  - `active` — `isSessionActive(status)` is true (existing rule).
  - `prereq` — `status` is terminal (e.g. `completed`/`failed`/`cancelled`) but `recoveryAvailable === false` for a documented prerequisite (e.g. recovery audit metadata missing).
  - `unknown` — `status` is null/undefined and `recoveryAvailable === undefined` (status query has not resolved).
  The spec scenario "Prerequisite reason explains what is missing" and "Unknown reason explains the temporary unavailability" should each be backed by a concrete input → reason mapping; otherwise the acceptance criterion is unmeetable without inventing conditions.
- **Impact if not fixed:** T-005 ships with either only `active` ever firing (silently dropping the spec's two other reasons) or with three reasons wired to ad-hoc conditions that don't match the spec scenarios.

### D-4: `session-cancel-trigger` testid preservation is not pinned in T-002 or T-004

- **Where:** `tasks.json` T-002 and T-004 acceptance criteria.
- **Conflict:** the existing `SessionPage.cancel.test.tsx:218` asserts `container.querySelector('[data-testid="session-cancel-trigger"]')`. Neither T-002 (header metadata rewrite) nor T-004 (Cancel demotion) explicitly requires this testid to survive. T-002's "Migrate the existing render specs" criterion is about selector rewrites for the new metadata testids; if T-002 accidentally drops the Cancel testid, the migration breaks.
- **Fix:** add an explicit acceptance criterion to T-004: "The Cancel button preserves `data-testid="session-cancel-trigger"` and `aria-label="Cancel session"` across the demotion, so `SessionPage.cancel.test.tsx` continues to find it." (And T-002 should say "does not modify the Cancel button — its demotion is owned by T-004" to keep the split clean.)
- **Impact if not fixed:** existing `SessionPage.cancel.test.tsx` regresses silently; the migration step "Run `npm run test:run -w packages/web` pass" will fail for a non-obvious reason.

## Moderate gaps (should fix before build)

### D-5: Sidebar conditional rendering is implied but not explicit

- **Where:** `specs/session-sibling-nav-dedup/spec.md:17` — "On viewport widths where the `SiblingSessionsSidebar` is not rendered" and `tasks.json` T-006 acceptance criterion 4 — "the sidebar is not rendered at this width (verified by absence of its `data-testid`)".
- **Conflict:** today the sidebar is *always* rendered and is hidden only via CSS (`xl:flex-row` on the parent at `SessionDetailShell.tsx:396`). Both the spec language ("is not rendered") and T-006's test ("absence of its `data-testid`") require conditional rendering, but design D7 only says "`SiblingSessionsSidebar` content remains untouched" — visibility vs. content is left ambiguous.
- **Fix:** D7 should explicitly state that `SessionDetailShell` conditionally renders `{siblingSidebar}` based on `useMediaQuery('(min-width: 1280px)')` (or equivalent), not just the header slot. The "content remains untouched" guarantee is preserved (the sidebar component itself doesn't change), but the rendering gate does. Add a sentence to T-006 explicitly noting the sidebar is conditionally rendered.

### D-6: Disabled Compact/Reset focusability is required by the spec but not called out in T-005

- **Where:** `specs/session-action-weight/spec.md:30–33` ("WHEN a user hovers or focuses a disabled Compact or Reset button / THEN a structured tooltip SHALL render") and `design.md:174` (open question, partially mitigated).
- **Conflict:** a `<button disabled>` is not focusable in browsers, so the "focus" path of the spec scenario cannot be triggered without making the button focusable (wrap in a focusable span, or use `aria-disabled` on an enabled `<button>` with click-guard). Design D6's mitigation says "spec ensures the disabled button remains focusable" but T-005's acceptance criteria don't pin the focusability requirement — only the existence of `data-disabled-reason` and the tooltip.
- **Fix:** add an acceptance criterion to T-005: "When Compact or Reset is disabled, the rendered control is keyboard-focusable (either via a `tabIndex={0}` wrapper inside `Tooltip` or via `aria-disabled` on an enabled button with a click-guard), so the tooltip can be triggered by focus as well as hover, per spec scenario 'Disabled reason renders a structured tooltip'."

### D-7: T-001 description references a non-existent time display on the sticky strip

- **Where:** `tasks.json` T-001 description: "Replace the call sites inside `SessionHeader` (last activity, probing indicator) and `StickySessionTitle` (when its time display lands)".
- **Conflict:** `specs/session-sticky-identity/spec.md:31` pins the strip's content to "exactly three pieces of information: the session name, the status badge, and the turn count" — no time. The current `StickySessionTitle` does not render a time. The "when its time display lands" parenthetical implies a future change that this issue does not make.
- **Fix:** drop the parenthetical from T-001's description; the call sites to migrate are `SessionHeader.lastActivityAt` and the `Checking since {probeSentAt}` indicator only.

### D-8: T-002 doesn't lock down "leave Cancel in place"

- **Where:** `tasks.json` T-002 notes ("Cancel and sibling-nav slot edits land in T-004 and T-006") but no acceptance criterion pins this.
- **Conflict:** T-002 rewrites the metadata row; if it accidentally drops or relocates the Cancel button, T-004 then has no clear before-state to demote from, and existing tests break.
- **Fix:** add an acceptance criterion: "T-002 MUST NOT alter the Cancel button's location, variant, or `session-cancel-trigger` testid; the cancel demotion is owned by T-004."

## Minor issues (nice to fix)

- **D-9.** `tasks.json` T-001 description claims "all six branches of the matrix". The design matrix has six tabular rows but live/finalizing/probing collapses regardless of threshold ("any"), so there are five distinct behavior branches. Either say "all six matrix rows" or "all five distinct behaviors"; both are fine, but the current phrasing invites confusion.
- **D-10.** `specs/session-time-display/spec.md:22` defines `absolute-relative threshold` with a `MORE RECENT OF completedAt OR lastActivityAt` rule, but neither T-001 nor T-002 currently pins which timestamp the helper should use as the anchor in the header. In practice the header currently renders `lastActivityAt`; if `completedAt` should be preferred for terminal sessions, that should be a T-001 acceptance criterion or an explicit note in the spec.
- **D-11.** Design D8 says "The `Sent` flash behavior is preserved on the transient `isSending` window only when `hasQueuedFollowup` is not supplied (backward compatibility); when `hasQueuedFollowup` is supplied, the `Sent` flash is replaced by the persistent queued indicator." This is fine, but `SessionFollowupComposer.test.tsx` currently has a "Sent" flash spec (`expect(...).toHaveTextContent(/sent/i)`); T-007 should explicitly assert that this existing spec still passes when `hasQueuedFollowup` is unset, so the backward-compatibility guarantee is testable.

## Verdict

The plan is well-structured, complete in capability coverage, and respects the epic-#49 boundary. But it ships **one critical defect (D-1)** — a spec/design contradiction on the sticky strip's initial visibility — and **three major defects (D-2, D-3, D-4)** — an unclear helper signature, two un-pinned disabled-reason triggers, and an unpreserved testid that would regress existing tests. These need fixing before build; the moderate and minor items are refinements the implementer can decide but would be cheaper to fix here.

<promise>FAIL</promise>
