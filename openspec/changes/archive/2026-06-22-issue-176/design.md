## Context

The Epic detail page (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`) renders linked issues as a flat list of homogeneous rows (`active / Backlog / P0`). The dependency structure between issues already lives in the domain — `Issue.PrerequisiteNumbers` (Issue.cs:96), the derived `CanStart` / `Blocker` (issue-start-readiness), and per-issue `IssuePrerequisiteSummary` (IssueInfo.cs:46) — but it is never projected to the epic read model and never rendered as a graph.

The server epic read path is `EpicQuerier.GetLinkedIssuesAsync` (EpicQuerier.cs:79), which already loads full `IssueInfo` objects (including `PrerequisiteNumbers`, `CanStart`, `Blocker`) via `_issuesQuery.ListAsync`, but the `LinkedIssueDto` mapping (EpicQuerier.cs:91) drops `PrerequisiteNumbers`. `EpicProgress.Build` (EpicProgress.cs:13) consumes the DTO but only reads `Status`, `Health`, `CanStart`, `StartBlocker`, `Priority`, `Number`, `Title` — so extending the DTO is provably non-breaking for progress/nextIssue/readyToMarkDone.

Constraints: epic-scoped issue counts are small (typically <20); the graph is a read-only projection; acyclicity is guaranteed by the domain (`issue-prerequisites` rejects cycles), so client-side cycle handling is defensive only.

## Goals / Non-Goals

**Goals:**
- Extend `LinkedIssueDto` with prerequisite-edge data so the client can render the DAG without per-issue fetches (including summaries for external prerequisites).
- Add a dependency-graph view to the Epic detail page, toggled against the existing Linked Issues list, rendering nodes (status-colored + readiness-marked) and directed prerequisite edges with client-side auto-layout.
- Make nodes navigable; distinguish external prerequisites; degrade to the list for 0–1 issues or a detected cycle.
- Preserve all existing `epic-board` / `epic-tracking` behavior bit-for-bit.

**Non-Goals:**
- No editing / adding / removing dependencies from the graph.
- No start control on graph nodes (the existing inline-start surface owns that).
- No server-side layout service, no large-graph (>50 nodes) optimization or folding.
- No layout tuning beyond dagre defaults; no persistence of the list⇄graph toggle preference.

## Decisions

### Decision 1: Extend `LinkedIssueDto` rather than add a dedicated `/dependency-graph` projection

Add `int[] PrerequisiteNumbers` (and a small set of external-prerequisite summaries — see Decision 2) directly to `LinkedIssueDto`. The data is already loaded in `GetLinkedIssuesAsync` via `_issuesQuery.ListAsync`; this is a mapping change, not a new data path.

**Alternatives considered:**
- *Dedicated `GET /epics/{id}/dependency-graph` returning nodes+edges.* Rejected: duplicates the linked-issue fetch, adds an endpoint + serializer, and forces the client to coordinate two queries (list for add/remove, graph for view). For <20 nodes the DTO extension is simpler and keeps a single source of truth.
- *Client-side fetch of each issue's prerequisites.* Rejected: N+1 queries and stale-cache risk.

### Decision 2: Resolve external-prerequisite summaries server-side, in `GetLinkedIssuesAsync`

A linked issue's `PrerequisiteNumbers` may reference issues outside the epic. To render those as distinct ghost nodes (spec: "at minimum number, title, status/delivery state"), `GetLinkedIssuesAsync` computes the set of prereq numbers not present in epic membership, bulk-loads those issues (reusing the `IssueQuerier` dictionary already built for members), and attaches an `IssuePrerequisiteRefDto` summary per external prereq. This mirrors the existing pattern in `IssueQuerier` (IssueQuerier.cs:670-700) for issue-detail prerequisite rendering.

`IssuePrerequisiteRefDto` (IssueInfo.cs:136) already carries `{Number, Title, Stage, Status}` and is reused as-is — no new DTO shape.

**Alternatives considered:**
- *Number-only ghost nodes (`#N external`).* Rejected: fails the spec's "number, title, and status/delivery state" minimum and gives the user no clue what the external dep is.
- *A separate `/epics/{id}/external-prerequisites` endpoint.* Rejected: one more round-trip and coordination point for no gain at this scale.

### Decision 3: Library choice — `@xyflow/react` + `dagre`

`@xyflow/react` (React Flow) for the node/edge canvas (React-native, handles pan/zoom/edge routing), `dagre` for automatic DAG layout (topological rank-based). Both run client-side; no server layout service.

**Alternatives considered:**
- *Cytoscape.js.* Rejected: heavier, less idiomatic React integration, its own layout plugins overlap with dagre.
- *Custom SVG + hand-rolled layered layout.* Rejected: reimplements dagre's rank assignment for no benefit; risk of broken edge routing.
- *vis-network.* Rejected: imperative API, canvas rendering conflicts with the DOM-based node styling needed for status/readiness markers.

### Decision 4: FSD placement — `widgets/epic-dependency-graph`

The graph is a composite, self-contained presentational block composed into the Epic detail page — matching the existing `widgets/kanban-board` precedent. It consumes the `LinkedIssue` entity type and `useEpic` query (no new data source); it owns graph-specific model (nodes/edges/layout derivation) and UI (the React Flow canvas + node components). A thin `features/epic-view-toggle` (or local state in `EpicDetailPage`) controls list⇄graph.

**Alternatives considered:**
- *`features/epic-dependency-graph`.* Rejected: FSD features pair a user task with mutations; the graph is read-only presentation.
- *Inline inside `pages/epic-detail`.* Rejected: would bloat the already 619-line page and prevent reuse/testing in isolation.

### Decision 5: Readiness marker is a pure client derivation

A single pure function maps `{ status, canStart, blocker }` (all already on `LinkedIssue` after the type extension) to exactly one of `can-start | waiting | in-progress | done`. Coloring comes from `status` (`backlog | in_progress | done | cancelled`). The waiting marker reads `blocker.kind === 'waiting-for'` → `blocker.issue.number` to show `#N`. This keeps the derivation in one place and matches how `EpicProgress.ReasonFor` already interprets the same blocker shape server-side.

**Note:** the web `LinkedIssue` type (types.ts:43) currently omits `canStart` / `startBlocker` even though the server DTO already sends them — extending the type to consume these is required and is additive (existing list-row consumers ignore the new fields).

### Decision 6: Degradation + cycle handling on the client

- **0–1 linked issues:** `EpicDetailPage` does not mount the graph widget; the list renders as today. Threshold check is a one-liner before the toggle.
- **Cycle detection:** before handing nodes to dagre, run a DFS cycle check over the edge set. If cyclic, set a flag that makes `EpicDetailPage` fall back to the list (with no broken layout). The domain guarantees acyclicity, so this is defensive — it exists so a future invariant violation never renders a corrupted graph.

### Decision 7: View-toggle state is local, not persisted

The list⇄graph selection lives in `EpicDetailPage` component state (or a small toggle component). The spec requires that switching not modify data; persisting the preference is a non-goal. Toggle UI is a segmented control in the "Linked Issues" Card header (EpicDetailPage.tsx:530).

## Risks / Trade-offs

- **[New JS bundle weight from @xyflow + dagre]** → Mitigation: lazy-load the graph widget (`React.lazy`) so the default list view stays light; the graph code only downloads when the user switches to it.
- **[`GetLinkedIssuesAsync` now also resolves external prereq summaries — extra queries]** → Mitigation: bounded by epic prerequisite count (small); reuses the `IssueQuerier` dictionary already built for members; single bulk load, not per-issue.
- **[Acyclicity assumption violated by a bug]** → Mitigation: defensive client DFS (Decision 6) falls back to the list; no corrupted render.
- **[External prerequisite that is archived / has no row]** → Mitigation: render as a ghost `#N (unresolved)` node; the summary loader returns a minimal ref when the issue row is missing.
- **[Extending `LinkedIssue` web type could affect other consumers]** → Mitigation: new fields are additive and optional; existing list-row rendering ignores them. Verified the only consumer is `EpicDetailPage`'s `LinkedIssueRow`.
- **[Server `LinkedIssueDto` is a serialized record consumed by tests]** → Mitigation: field is added at the end with a default (`int[] PrerequisiteNumbers = []`), matching the existing default-optional pattern (`CanStart = false`); existing test record mirrors update mechanically.

## Migration Plan

1. **Server DTO + mapping** (additive, backward-compatible):
   - Add `int[] PrerequisiteNumbers = []` and `IReadOnlyList<IssuePrerequisiteRefDto> ExternalPrerequisites` (or a single flattened prerequisite-summary list) to `LinkedIssueDto`.
   - In `GetLinkedIssuesAsync`, populate `PrerequisiteNumbers` from `issue.PrerequisiteNumbers` and resolve external summaries from the already-loaded `IssueQuerier` dictionary.
   - Add/extend server specs in `EpicApiSpecs.cs` / `EpicLifecycleSpecs.cs` to assert the new fields are present and that `EpicProgress` outputs are unchanged.
2. **Web entity + query**: extend `LinkedIssue` type with `prerequisiteNumbers`, `canStart`, `startBlocker`; confirm the epic query passes them through.
3. **Web widget**: add `widgets/epic-dependency-graph` (model: nodes/edges/layout/readiness derivation + cycle check; ui: React Flow canvas + node components), lazy-loaded.
4. **Page integration**: add segmented list⇄graph toggle in the Linked Issues Card header; gate graph mount on `linkedIssues.length >= 2` and no detected cycle.
5. **Dependencies**: add `@xyflow/react` and `dagre` to `packages/web`.

**Rollback:** the DTO field is additive; reverting the frontend + DTO extension leaves the server fully functional (existing list view unchanged). No DB migration is involved — `PrerequisiteNumbers` is already persisted on `Issue`. No feature flag is required because the change is purely additive UI.

## Open Questions

- **External-prerequisite shape on the DTO:** flatten prerequisite summaries into each `LinkedIssueDto` (only the external ones, keyed by number) vs. a top-level `externalPrerequisites` map on `EpicDetailDto`. Leaning toward per-linked-issue flattening (keeps `LinkedIssueDto` self-contained for edge rendering); confirm during implementation.
- **Should the list⇄graph toggle preference persist across epics (localStorage)?** Out of scope here (non-goal); revisit if users complain about re-selecting.
- **Edge routing style (smooth bezier vs. straight) and node sizing.** Default to React Flow's smoothstep edges + fixed-width nodes; defer polish to a follow-up since layout tuning is a non-goal.
