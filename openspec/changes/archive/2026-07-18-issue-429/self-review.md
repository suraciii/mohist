# Self-Review — Issue 429 (Session transcript 导航与错误定位)

Reviewer reviewed `proposal.md`, `design.md`, `tasks.json`, and all four
`specs/*/spec.md` against the issue and the existing code surface
(`packages/web/src/widgets/session-transcript/`,
`packages/web/src/pages/session/ui/SessionDetailShell.tsx`,
`packages/web/src/pages/session/data/SessionDataSource.ts`). Factual claims
about the codebase were spot-checked; findings below are limited to genuine
inconsistencies, gaps, or ambiguities that an implementer would otherwise
have to reconcile or guess.

## What holds up

- The four capability boundaries map cleanly to the three issue acceptance
  criteria plus the shared highlight behavior. Each spec uses SHALL/MUST,
  4-hashtag scenarios, and is self-contained (no `## ADDED/MODIFIED/REMOVED`
  headers, no cross-spec references).
- Verifiable codebase claims are accurate: `data-turn-id`/`data-turn-index`
  on turns, `data-tool-call-id`/`data-tool-state` on tool rows
  (`tool-views/index.tsx:233-234`), the canonical click-to-jump pattern in
  `CurrentActivityBar.tsx:14-20`, the active-tool selector shape in
  `select-active-tool-call.ts`, the `siblingSidebar` occupation in
  `useIssueSessionDataSource.tsx:322`, the container-level
  `contentVisibility: 'auto'` at `TurnList.tsx:26`, and the `useNow`
  time-injection seam from #428.
- Task graph is a valid DAG; T-003 → T-002 priority ordering is sound; the
  bundling of locate/highlight infra + first launcher (T-002) follows #428's
  precedent of "infra + first consumer in one task".
- Lazy-render decision (D6, per-turn granularity) is the most defensible
  choice; alternatives are correctly rejected.

## Blocking findings (must fix before build)

### B1: Proposal promises `ErrorPartView` as a locatable jump target; specs / design / tasks silently drop it

`proposal.md:27` states:

> `ui/AssistantParts.tsx` `ErrorPartView` gains a locatable id so individual
> error parts can be jump targets too (failed tool rows already carry
> `data-tool-state="failed"`).

None of the four specs, no decision in `design.md`, and no task in
`tasks.json` implement, reference, or even acknowledge this. The locate-flow
design (D3 step 5) keys the highlight registry by `toolCallId ?? turnId`,
which would not cover `DisplayErrorPart` (it has neither). Either remove the
proposal claim or add a spec requirement + design decision + task AC for
`ErrorPartView` jump targets. As written, a reader of the proposal will
expect a feature the rest of the plan does not deliver.

### B2: `transcript-error-jump` spec doesn't cover "first failed tool lives inside a collapsed context group"

Today, `ContextGroupView` only mounts its inner `ToolRowView`s when
`expanded` is true (`tool-views/index.tsx:397` — `{expanded && (…)}`). So
when the first failed tool in document order is inside a collapsed context
group, `querySelector('[data-tool-state="failed"]')` returns null. The
`transcript-error-jump` spec's "Activation is a no-op when no failed row is
mounted" scenario (lines 51–59) would then swallow this case as a no-op —
directly contradicting T-002's acceptance criterion "Locating a target
inside a collapsed context group expands the containing group before
querying the inner row".

The mini-timeline spec explicitly requires context-group descent
(lines 52–65), but the error-jump spec does not. The two launchers share the
locate hook (per design D3 / D5), so they share the failure mode. Add a
scenario to `transcript-error-jump/spec.md` requiring activation to expand
the containing group of the first failed tool when needed, and tighten the
"no-op" scenario so it applies only when the projection genuinely contains
no failed tool (rather than when no failed row happens to be mounted).

### B3: `selectFailedToolCalls` return shape loses the `groupId` locate needs

Both design D5 and T-002 specify `selectFailedToolCalls(turns):
DisplayToolPart[]`. But the locate flow (D3 step 1) requires
`target.groupId` to expand the containing context group before querying the
inner row. A flat `DisplayToolPart[]` carries no group context — the group
id is a property of the *containing* `DisplayContextGroupPart`, not of the
inner tool part. Neither the design nor the task says how `groupId` is
recovered for a failed tool that lives inside a group.

Either change the selector's return type to something like
`Array<{ tool: DisplayToolPart; groupId?: string }>` (matching the
projection's `id: ctx-${tools[0].id}` convention at
`session-transcript-display.ts:324`), or specify a separate lookup. Without
this, the implementer must invent the contract.

### B4: Node-kind overlap / dedup is unspecified

Design D2 emits nodes independently per kind (failed / file-change /
read-explore). A single tool call can qualify for more than one: e.g. a
failed edit (`status === 'failed'`, `changedFiles` non-empty, verb family
`edit`) would emit both a red `failed` node AND a green `file-change` node
for the same call. A read-family call that fails would emit both red and
gray. The mini-timeline spec (`specs/transcript-mini-timeline/spec.md`)
says event nodes "SHALL be classified into exactly three kinds" but never
says whether one call may produce multiple nodes, and if so how they render
(stack? collapse? priority order?). Add a requirement that pins down the
dedup rule (e.g. `failed` wins over `file-change` wins over `read-explore`,
or nodes are mutually exclusive by precedence, or multiple nodes per call
are explicitly allowed and rendered as a stacked marker).

## Important findings (should fix)

### I1: `ContextGroupView` does not receive a group id today

`ContextGroupViewProps` (`tool-views/index.tsx:331-336`) is `{ title, tools,
hasError, now }`. The projection assigns each group a stable id
(`session-transcript-display.ts:324`) but it is not threaded through
`AssistantParts.tsx:141` (`<ContextGroupView …>` receives no `id`). T-002's
plan to "register an expander on each `ContextGroupView`" into an
`expansionRegistry: Map<groupId, () => void>` therefore requires either
threading a new `groupId` prop through `AssistantParts` or recomputing the
id at the view layer. Neither design nor task acknowledges this prop change.
Add a one-line note to design D3 / T-002 output describing how the group id
reaches `ContextGroupView`.

### I2: T-001 and T-002 both modify `TurnList.tsx` but T-002 has no `dependsOn: ["T-001"]`

T-001's output is `TurnList.tsx (per-turn contentVisibility + containIntrinsicSize)`.
T-002's output is `TurnList.tsx (highlight registration on TurnItem)`. T-002
has `dependsOn: []`. Per the tasks.json rules, `dependsOn` reflects output
consumption; here T-002 does not consume T-001's output, but the runner may
parallelize two same-priority-independent tasks (the rules do not forbid
it), and both edit the same file's `TurnItem`. T-002's notes acknowledge
this informally ("if T-001 has not landed, coordinate"), but coordination is
non-deterministic. Either add `dependsOn: ["T-001"]` to T-002 to force
sequencing, or split the `TurnItem` edits so each task owns a different
facet of the component unambiguously.

### I3: Mini-timeline spec doesn't address `cancelled` tool calls

`DisplayToolPart.status` is `'pending' | 'running' | 'completed' | 'failed'
| 'cancelled'` (`session-transcript-display.ts:67`). The mini-timeline spec
requires a failed-event node for `failed` calls but is silent on `cancelled`
calls. The issue's user voice only mentions "失败工具", so scoping out
`cancelled` is defensible — but the spec should say so explicitly (e.g.
"Cancelled tool calls SHALL NOT produce a dedicated event node") rather
than leave it implementer-discretionary.

### I4: Lazy-render spec terminology "deferred or skipped rendering" is inaccurate for `content-visibility: auto`

`transcript-render-performance/spec.md` requirement 2 says a row that is
off-screen "SHALL be a candidate for deferred or skipped rendering". CSS
`content-visibility: auto` skips paint and layout for off-screen elements
but does **not** skip React rendering — the element (and its descendants)
are still mounted in the DOM, still react to state changes, and still
expose their anchors to `querySelector`. The spec's wording could mislead a
future maintainer into thinking React rendering is gated. Tighten the
language to "deferred paint and layout" (which is what
`content-visibility: auto` actually does) and reserve "rendering" for React
mounting.

### I5: Turn-node highlight scope is unspecified

When a turn node is activated, the locate flow targets `data-turn-id`,
which lives on the `TurnItem` root (`TurnList.tsx:60`) — a container that
can be thousands of pixels tall (a turn with many tool rows). The
`transcript-jump-highlight` spec says "the row SHALL receive the transient
highlight" but never defines whether turn-jump highlights the entire
`TurnItem` or just the `TurnDivider` (the visually meaningful "row" for a
turn). Highlighting an entire tall turn would look broken. Design D4 also
doesn't address this. Either pin the turn-jump highlight target to the
`TurnDivider` element in the spec/design, or accept that turn-node jumps
highlight a different (smaller) element than tool-row jumps.

## Minor findings (worth a note, not blocking)

### M1: `use-highlight.ts` hook appears in T-002 output but is not named in design D4

Design D4 describes the highlight behavior conceptually ("a row-local
effect with a time-injected auto-dismiss") without naming a hook. T-002's
output list adds `widgets/session-transcript/model/use-highlight.ts`. The
divergence is acceptable (tasks can be more granular than design), but
cross-linking the two would help reviewers.

### M2: `CONTEXT_TOOL_NAMES` breadth vs. spec's "for example read/grep/glob/search"

Design D2 says read-explore nodes are derived from the existing
`CONTEXT_TOOL_NAMES` set, which includes `read`, `read_file`, `glob`,
`grep`, `search`, `list`, `membrowse`, `memread`, `memsearch`,
`search_files` (`session-transcript-display.ts:117-120`). The
mini-timeline spec says "for example read, grep, glob, search". The two are
consistent in spirit but the spec's "for example" leaves `list` /
`membrowse` / etc. ambiguous. Consider an explicit "the exploratory-read
kind is exactly the `CONTEXT_TOOL_NAMES` set" in the spec to remove the
ambiguity.

### M3: Risk list missing — highlight on a row inside an off-screen content-visibility-hidden turn

Design D6 establishes that off-screen turns keep their anchors queryable.
D7 asserts the locate flow with the turn already on-screen (jsdom limit).
But the realistic user path is: turn is off-screen → mini-timeline click →
locate → `scrollIntoView` triggers paint → highlight applies. This works in
theory (scrollIntoView forces layout/paint on `content-visibility:auto`
elements) but is not in the risk list and has no browser-track
verification plan. Worth adding as a risk note.

### M4: Live mini-timeline during streaming is an open question without a corresponding risk note

Design's Open Questions says the mini-timeline consumes `displayTurns`
reactively and updates live during a running session, but the Risks section
doesn't note that frequent projection re-computation + rail re-render
during streaming could itself become a perf regression on long sessions.
Add a one-line risk or a note tying it to the lazy-render capability.

## Verdict

The plan is structurally sound and most codebase claims check out, but B1
through B4 are genuine cross-artifact inconsistencies (dropped scope,
spec/task contradiction, under-specified return shape, undefined
dedup rule) that an implementer cannot resolve without making product
decisions on behalf of the reviewer. These must be reconciled before build.

<promise>FAIL</promise>
