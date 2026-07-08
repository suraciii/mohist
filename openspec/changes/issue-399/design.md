## Context

The dashboard is Mohist's first screen and highest-frequency entry point, but it
does not answer the question an owner opens it for: *what needs my attention
right now?* This issue reworks only the dashboard's information hierarchy and
visible content model. **No backend/workflow/runner behavior changes** — all data
is sourced from existing queries. The shared status-presentation baseline landed
in issue 398 (`shared/status-presentation/`) is the visual contract every state
routes through.

### Current state (verified by code reading)

The page composes three stacks top-to-bottom
(`pages/dashboard/ui/DashboardPage.tsx:58-83`):

1. `FactoryStatusHeadline` — compact stat strip (`dashboard-headline`).
2. `AttentionHero` — the needs-attention surface (`dashboard-hero`, emits
   `dashboard-zone-attention` internally).
3. A responsive grid of two equal-weight `DashboardZone` wrappers
   (`DASHBOARD_ZONES` = `pulse` + `digest`, `DashboardPage.tsx:14-17`) — active
   sessions and recent history, **side by side regardless of state**.

Structural defects this issue addresses:

1. **Attention model is incomplete and split.** `deriveAttentionItems(issues,
   _agentStatus)` (`entities/issue/model/attention.ts:22-69`) produces only four
   issue-derived kinds (`approval-needed`, `integration-failed`, `interrupted`,
   `blocked`). The second parameter is named `_agentStatus` and **completely
   ignored** (asserted at `attention.test.ts:406-425`). Runner state is bolted on
   *inside* `AttentionHero`: `runnerDown = agentStatus?.runnerAvailable === false`
   (`AttentionHero.tsx:58`) renders a separate `RunnerDownEntry`
   (`:225-266`). **Runner capacity-limited is detected nowhere** — a saturated
   runner (active ≥ max) is invisible until the owner opens the runner board.

2. **Active production shows sessions, not issues.** `PulseZone` renders only
   `AgentActivitySession` rows with `status === 'active'`
   (`widgets/coder-session/model/activity-cards.ts:84`), sourced solely from
   `useAgentActivity`. An in-progress issue paused between workflow stages (no
   active session) **vanishes from the first screen**, even though
   `IssueStatus.InProgress` (`entities/issue/model/issue.ts:7`) is the canonical
   "running" status and `FactoryStatusHeadline` already counts such issues as
   "in flight" (`factory-status.ts:34-36`).

3. **Capacity is a level-3 concern living inside level 2.** Runner slot usage is
   a header *inside* `PulseZone` (`pulse-slots`, `PulseZone.tsx:16-26`), not its
   own level. The spec requires capacity to be a distinct level ordered before
   recent history.

4. **Two divergent capacity feeds.** `AgentStatus.capacity = { active, max }`
   (`entities/agent/model/types.ts:27-30`) and
   `AgentActivity.summary.slots = { active, max }` (`:208-211`) carry the same
   meaning from the same runner; consumers pick one ad hoc (Pulse uses slots,
   everyone else uses `agentStatus`).

5. **Empty zones dominate.** `DashboardZone` forces `min-h-[160px]`
   (`DashboardZone.tsx:13-18`) with **no collapse behavior**. When nothing is
   running, two empty dashed boxes hold the most prominent area below the hero.

6. **No "owner action needed" cue exists.** A search for
   `ownerAction|actionNeeded|needs_owner` across `packages/web/src` returns no
   matches. The closest signals are `IssueHealth.Blocked`/`Interrupted` and
   `approvalState.status === 'awaiting'`, all surfaced only in the attention
   zone, never as an inline cue on a running issue.

### Constraints / stakeholders

- **Web-only** (`packages/web/src`). No server / runner / CLI / API / domain
  change. No new dependency.
- **Risk: medium.** Touches a high-frequency entry point and several widgets, but
  regressions are confined to dashboard presentation/hierarchy. Mitigated by
  preserving the stable test-id contract (`dashboard-zone-attention`/`-pulse`/
  `-digest`, `factory-status-headline`, `attention-item*`, `runner-down-*`,
  `pulse-compact-*`, `digest-row`) and routing every state through the
  status-presentation layer from issue 398.
- Product terms (issue, workflow, stage, health, approval, runner, session, epic)
  and existing ARIA contracts are preserved.

## Goals / Non-Goals

**Goals:**

- **G1:** One strict zone priority on the first screen: needs-attention → active
  production → capacity → recent history. Lower-priority zones yield visual
  prominence when a higher-priority zone has content.
- **G2:** The needs-attention model surfaces every owner-action state in one
  place: approval gates, blocked, interrupted, integration-failed,
  runner-unavailable, **and** runner capacity-limited (active ≥ max, runner up).
- **G3:** Active production shows running (in-progress) issues with their current
  workflow stage and an owner-action-needed cue — not only issues with an active
  agent session — so work paused between stages stays visible.
- **G4:** Empty zones collapse (no reserved fixed-height boxes) when
  higher-priority content exists.
- **G5:** A concise ready state replaces the large empty layout when nothing needs
  attention and nothing is active.
- **G6:** The headline stays a compact strip subordinate to the attention zone.

**Non-Goals:**

- No push notifications, event subscriptions, or inbox mechanism.
- No Settings / runner-configuration redesign; no replacement of the issue board.
- No change to how issues, workflow runs, approval state, or runner capacity are
  computed.
- No recoloring or new design tokens (consumed as-is from issue 398).

## Decisions

### D1 — Attention model absorbs runner state; becomes a discriminated union

**Decision.** `deriveAttentionItems(issues, agentStatus)` stops ignoring its
second argument and becomes the **single** source of every needs-attention state,
issue- or runner-derived. `AttentionHero` stops bolting on `RunnerDownEntry` and
renders model items uniformly.

`AttentionItem` evolves from a flat interface into a discriminated union, because
runner items fundamentally differ from issue items (no issue reference, a fixed
link target `/activity`, a different glyph):

```ts
type AttentionItem =
  | { kind: 'approval-needed' | 'integration-failed' | 'interrupted' | 'blocked'
      issueNumber: number; issueId: string; label: string; detail?: string }
  | { kind: 'runner-unavailable' | 'runner-capacity-limited'
      label: string; detail?: string }
```

Runner items are emitted **after** issue items (preserving today's visual order
where the runner-down row follows the issue rows). Issue classification order
(approval → integration-failed → interrupted → blocked, first-match-wins, dedup
by id) is unchanged.

**Rationale.** The proposal explicitly states "`deriveAttentionItems` grows to
include runner capacity-limited." Pulling runner-unavailable in too removes the
two-code-path split (model vs. hero special-case) and makes the spec requirement
"runner-unavailable surfaces even when no issue-derived items exist" structural
rather than incidental. A discriminated union lets the type system enforce that
issue items carry an issue reference and runner items do not — eliminating the
"optional `issueNumber` that is sometimes undefined" smell a flat extension would
introduce.

**Alternatives considered.**

- *A1 — Keep `deriveAttentionItems` issue-only; add a parallel runner-attention
  derivation merged inside `AttentionHero`.* Rejected: preserves the exact
  two-path split that today lets capacity-limited slip through unimplemented;
  the spec wants one needs-attention model.
- *A2 — Flat extension: add the two runner kinds, make `issueNumber`/`issueId`
  optional, branch on presence.* Rejected: branching on "is the issueNumber
  present" is a runtime smell the union removes at the type level, and it weakens
  the issue-item contract (callers could no longer rely on `issueNumber` being
  defined for issue kinds).

### D2 — Capacity-limited detection: one source, one guard

**Decision.** Capacity-limited is derived from `agentStatus.capacity`
(`{ active, max }`), the same field `runner-unavailable` and the headline already
read — **not** from `AgentActivity.summary.slots`. It fires iff:

```
runnerAvailable !== false  &&  max > 0  &&  active >= max
```

Precedence: `runner-unavailable` (emitted when `runnerAvailable === false`)
suppresses `runner-capacity-limited` — a down runner is the stronger signal and
its capacity is moot. The `max > 0` guard prevents a 0/0 (unconfigured/empty)
runner from perpetually reading "at capacity."

`AttentionHero`'s existing `defaultAgentStatus` fallback
(`AttentionHero.tsx:133-139`) has no `runnerAvailable`, so it is treated as
runner-up and will not fire either runner item — preserving current behavior in
the loading/no-data path.

**Rationale.** Unifying capacity on `agentStatus.capacity` removes the dual-feed
inconsistency (defect 4) and keeps the attention model coupled to the runner
status object it already consumes. The activity feed may be loading or empty
independently of the runner; tying an attention signal to it would make the
signal flaky.

**Alternatives considered.**

- *A1 — Source capacity from `AgentActivity.summary.slots`.* Rejected: couples an
  attention signal to the activity feed's load state and keeps two capacity feeds
  alive.
- *A2 — Emit both runner-unavailable and capacity-limited simultaneously.*
  Rejected: redundant and confusing; unavailable is strictly stronger.

### D3 — Capacity becomes its own level, sourced from one feed

**Decision.** Extract runner capacity usage out of `PulseZone` into a dedicated,
subordinate capacity level positioned **between** active production and recent
history. `PulseZone` becomes purely the active-work list and loses its
`pulse-capacity-header` / `pulse-slots`. The new capacity level reads
`agentStatus.capacity` (the same source as D2) and renders a compact usage strip
(`dashboard-zone-capacity`). It collapses (renders nothing) when capacity data is
absent (`max === 0` / undefined), so it never becomes an empty reserved box.

**Rationale.** The spec mandates "runner capacity usage MUST appear as its own
level, ordered before recent history and separate from it." Today it is a header
inside active production (defect 3). Extracting it also removes the last consumer
of `AgentActivity.summary.slots` for capacity, completing the single-feed
unification of D2.

**Alternatives considered.**

- *A1 — Keep capacity as a header inside `PulseZone` and treat "distinct level"
  as priority-only.* Rejected: the spec says "separate from it"; a header inside
  active production is not separable or independently collapsible.
- *A2 — Fold capacity into the headline.* Rejected: the headline is a compact
  subordinate strip (G6) and already has five stats; capacity usage is a level-3
  production concern, not a headline metric.

### D4 — Active production is issue-led, session-enriched

**Decision.** Rework `PulseZone` to enumerate **running issues** —
`status === IssueStatus.InProgress && health !== 'done' && health !==
'cancelled'` (the same predicate `factory-status.ts:34-36` uses for "in flight")
— sourced from `useIssues`. Each running-issue row shows:

- the issue title and a link to `/issues/{number}` (preserving `pulse-compact-*`
  test-ids),
- its **current workflow stage** from `issue.workflowStage`
  (`entities/issue/model/issue.ts:88`), rendered with the categorical
  stage-identity palette already in `CompactSessionCard` (`:7-25`, kept
  categorical per issue-398 D6),
- an **owner-action-needed cue** when the issue needs the owner,
- optional session telemetry (tokens, task progress, context health) joined from
  `useActivityCards` by issue number **when an active session exists**.

When no active session is present (work paused between stages), the row renders
with stage + cue only — no telemetry — so it stays visible.

The owner-action cue is defined as: the issue would also produce an attention
item, i.e. it is awaiting approval, blocked, interrupted, or integration-failed.
This predicate is factored as `issueNeedsOwnerAction(issue)` and shared with the
issue classification inside `deriveAttentionItems` (D1) so the cue and the
attention entry can never disagree.

**Rationale.** Defect 2: Pulse is session-led today, so in-progress issues
paused between stages vanish. Making it issue-led makes "what is in the pipeline"
the primary content and the session an enrichment — exactly the spec's
"running issues, not only issues with an active session." Reusing the
factory-status in-flight predicate keeps one definition of "running" across the
dashboard. Sharing `issueNeedsOwnerAction` with the attention model guarantees
the inline cue matches the prioritized attention entry (no drift).

**Alternatives considered.**

- *A1 — Keep Pulse session-led; add a separate "running issues" list beside it.*
  Rejected: two parallel active-work lists on one first screen; the spec wants
  one active-production zone.
- *A2 — Dedupe: hide owner-action issues from active production (show them only
  in the attention zone).* Rejected: the spec explicitly requires in-progress
  issues — including paused ones — to stay visible on the first screen; the cue
  is the disambiguator, not removal. (See Open Questions.)

### D5 — Empty zones collapse; concise ready state when idle

**Decision.** `DashboardPage` stops rendering the unconditional two-box grid and
instead renders an explicit priority stack with collapse rules:

```
headline  (always, compact strip)
attention        — render iff hasAttention
active-production— render iff hasActiveWork (running issues or active sessions)
capacity         — render iff capacity data present (max > 0)
recent-history   — render iff digest has items
```

- **Collapse:** remove `DashboardZone`'s fixed `min-h-[160px]`
  (`DashboardZone.tsx:13-18`); zones size to content; a zone with no content
  returns `null` (no reserved box). This satisfies "empty zones collapse when
  higher-priority content exists" structurally — an empty zone simply is not
  there.
- **Ready state:** when `!hasAttention && !hasActiveWork` (no needs-attention
  states and no running/active issues), render a single concise ready block
  instead of the hero-plus-two-empty-boxes layout. The headline stays as the
  compact strip above it. Recent history (digest), if it has items, may appear as
  a small subordinate strip beneath the ready block (it is real content, not an
  empty zone); if the digest is also empty, the ready block stands alone.

`hasAttention` and `hasActiveWork` are computed once in `DashboardPage` from the
shared hooks (`useIssues`, `useAgentStatus`) and the same predicates used by the
hero and pulse, so the page, the hero, and the pulse cannot disagree about
whether attention or active work exists.

**Rationale.** Defects 5 and the spec's ready-state requirement. Making collapse
the default (empty ⇒ not rendered) is simpler and more robust than conditional
min-heights. Centralizing `hasAttention`/`hasActiveWork` prevents the page and
its children from rendering contradictory states.

**Alternatives considered.**

- *A1 — Keep `min-h-[160px]` but hide empty zones via a prop.* Rejected: leaves
  the reserved-box behavior as the default and requires every zone to opt out.
- *A2 — Ready state suppresses the digest too.* Rejected: recent history is real
  content, not an empty zone; hiding it would waste the only useful signal in an
  otherwise-idle dashboard.

### D6 — Headline stays subordinate

**Decision.** `FactoryStatusHeadline` is unchanged in composition and remains the
first element. The design asserts (in `DashboardPage` spec tests) that it stays a
**compact strip** positioned above the attention zone and is never more visually
prominent than the attention zone when attention content exists. No styling
enlargement is introduced; the attention zone retains its `data-family` hero
treatment from issue 398.

**Rationale.** G6 and the spec's headline-subordination requirement. Already true
today; this decision keeps it true by construction and locks it with a test.

### D7 — Tests: spec for hierarchy, unit for the model

**Decision.** Two tracks per `design/testing.md` (web track), no new e2e/a11y:

1. **Unit — `entities/issue/model/attention.test.ts`.** Add: capacity-limited
   fires at `max > 0 && active >= max` with runner up; does **not** fire below max
   or when `max === 0`; runner-unavailable suppresses capacity-limited;
   runner-unavailable still emits with an empty issue array; runner items sort
   after issue items; the union type is exhaustive over kinds. The existing
   "agentStatus is ignored" test is inverted (it is now consumed).
2. **Spec — `pages/dashboard/ui/DashboardPage.test.tsx`.** Add/adjust: the four
   levels appear in priority order (attention before active-production before
   capacity before digest) via `compareDocumentPosition`; an empty lower-priority
   zone is absent from the DOM when higher-priority content exists; the concise
   ready state renders (and the empty pulse/digest boxes do not) when idle; the
   headline precedes and is subordinate to the attention zone.
3. **Spec — `widgets/attention-hero/ui/AttentionHero.test.tsx`.** Replace the
   bolted-on `RunnerDownEntry` assertions with model-driven runner-item
   assertions: capacity-limited surfaces as an attention item; runner-unavailable
   still surfaces (test-ids `runner-down-entry`/`-message`/`-link` preserved for
   the unavailable kind); a `capacity-limited` entry appears under saturation.
4. **Spec — `widgets/dashboard-pulse/ui/PulseZone.test.tsx`.** Rewrite for the
   issue-led model: an in-progress issue with no active session stays visible
   with its stage; the owner-action cue distinguishes action-needed from
   normally-running; active sessions still enrich matching issues; the capacity
   header is gone (moved to the capacity level — covered by a new capacity-level
   test).

All time-dependent logic continues to use `vi.setSystemTime`; no real
network/process/DB; existing per-file `makeIssue`/`makeAgentStatus`/`makeSession`
factories are extended (no new shared cross-file helper is introduced, matching
current convention).

**Rationale.** The testing principles require spec vs. unit separation and ban
real time/external deps. The hierarchy behavior is a spec (page-level, DOM
order); the attention model is a unit (pure function). Preserving the stable
test-id contract keeps the rewrite low-blast-radius.

## Risks / Trade-offs

- **[Attention model signature change]** `deriveAttentionItems` now consumes
  `agentStatus`; every caller and test updates. -> Web-only, two callers
  (`AttentionHero`, tests). Contained.
- **[Capacity feed unification]** Rewiring Pulse off `AgentActivity.slots` onto
  `agentStatus.capacity` could surface a numeric discrepancy if the two feeds
  ever disagree. -> Both originate from the same runner and should agree; if they
  do not, that is a pre-existing data bug this change exposes rather than
  introduces. Flagged in Open Questions.
- **[Issue appears in two zones]** A blocked in-progress issue shows in both the
  attention zone and active production (with the owner-action cue). -> Intentional
  (spec wants paused/in-progress work visible); the cue disambiguates. See Open
  Questions for whether to later dedupe.
- **[Capacity level noise]** A new always-eligible level could add clutter when
  the runner is idle. -> It collapses when `max === 0`/absent, and in the
  fully-idle ready state it folds into the concise ready line rather than
  rendering a standalone box.
- **[Ready-state digest placement]** Showing the digest beneath the ready block
  is a judgment call. -> It is subordinate and only present when it has real
  items; if it reads as clutter, it can be hidden (Open Questions).
- **[Large touch surface]** Several widgets change. -> Mitigated by preserving the
  test-id contract (D7) and routing every state through the issue-398
  status-presentation layer.

## Migration Plan

- **No API/domain/schema migration.** Pure presentation/hierarchy change,
  web-only, no new dependency.
- **Order (each step independently reviewable):**
  1. **Model (D1/D2):** evolve `AttentionItem` into the union; make
     `deriveAttentionItems` consume `agentStatus` and emit `runner-unavailable` /
     `runner-capacity-limited` with the D2 guard and precedence. Update
     `attention-treatment.ts` for the two new kinds (route through
     `statusTreatment('runner', ...)`: unavailable → `danger`/solid glyph to match
     today's `RunnerDownEntry`, capacity-limited → `warning`).
  2. **Hero (D1):** delete the special-cased `RunnerDownEntry`; render runner
     items from the model, preserving `runner-down-*` test-ids for the
     unavailable kind.
  3. **Capacity level (D3):** add the `dashboard-zone-capacity` surface sourced
     from `agentStatus.capacity`; remove the capacity header from `PulseZone`.
  4. **Active production (D4):** rework `PulseZone` to be issue-led with stage +
     owner-action cue + optional session enrichment; add `issueNeedsOwnerAction`.
  5. **Hierarchy + ready state (D5/D6):** rewrite `DashboardPage` into the
     priority stack with collapse; remove `DashboardZone`'s `min-h-[160px]`; add
     the ready-state branch; compute `hasAttention`/`hasActiveWork` centrally.
  6. **Tests (D7):** update unit + spec tests per above.
- **Verification.** `npm run typecheck -w packages/web` and
  `npm run test:run -w packages/web` green throughout; manual sweep of the four
  states (has-attention, active-only, idle/ready, capacity-limited) in light and
  dark theme.
- **Rollback.** Revert the commit(s); no persistent state change, no migration to
  undo.

## Open Questions

- **Owner-action issues in two zones.** Keep blocked/awaiting issues visible in
  active production (with the cue) per the spec, or dedupe them out of active
  production so they live only in the attention zone? Design keeps them visible
  (cue disambiguates); confirm.
- **Capacity feed agreement.** If `AgentStatus.capacity` and
  `AgentActivity.summary.slots` ever disagree, which is authoritative? Design
  treats `agentStatus.capacity` as authoritative on the dashboard; confirm this is
  the runner's source of truth (or pick the other and adjust D2/D3).
- **Runner-unavailable visual weight.** Today's `RunnerDownEntry` uses a solid
  `danger` glyph; capacity-limited reads as `warning`. Confirm capacity-limited
  should be visually softer than unavailable (stronger signal), not equal.
- **Capacity level in the ready state.** Fold into the concise ready line, or
  render the standalone `dashboard-zone-capacity` even when idle? Design folds it
  in; confirm.
- **Recent-history placement when idle.** Show the digest as a subordinate strip
  under the ready block, or hide it entirely for maximum conciseness? Design
  shows it when it has items; confirm.
