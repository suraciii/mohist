## Context

Issues already persist a canonical repository assignment and optional `ParentIssueNumber`. The server list/detail read model resolves repository metadata, a minimal parent reference, and `ChildIssuesSummary` grouped by child status. Composite lifecycle and repository validation are already authoritative in the Issue domain and repository coordinator.

The Web currently receives those additive fields but uses them only as static detail metadata. Board query state supports priority, labels, title search, and sort, but not repository. Cards do not show repository or composite progress. The detail page always starts workflow-related queries and renders workflow surfaces. New Issue can choose a repository in multi-repository projects, but initializes only the single-repository case; its API type cannot submit `parentIssueNumber`, and it has no parent selector.

This change is a read and interaction layer over the behavior delivered by issues 417 and 419. It must not reinterpret lifecycle, parent eligibility, repository binding, or workflow decisions in the Web. It must preserve project-scoped identities, existing API envelopes, responsive board behavior, and Feature-Sliced Design dependency direction.

Stakeholders are operators scanning and creating work in the Web UI, and maintainers of the Issue API/read projection and Web issue surfaces.

## Goals / Non-Goals

**Goals:**

- Add one server-owned composite read projection that supports parent cards and parent details without client-side relationship joins.
- Show persisted repository assignment and composite progress on cards and details.
- Add shareable repository filtering that composes with existing board state on desktop and mobile.
- Render parent details as a composite overview and avoid workflow requests and controls that do not apply to parents.
- Let New Issue submit repository and parent assignments together while preserving server validation as the final authority.
- Cover the read projection and user-facing behavior at the lowest useful test layers.

**Non-Goals:**

- Changing parent/child invariants, composite advancement, status aggregation, repository locking, or workflow behavior.
- Adding relationship editing, drag-and-drop, or parent reassignment outside New Issue.
- Adding cross-repository diff aggregation or multi-checkout.
- Adding a new persistence table, relationship aggregate, client state store, or external dependency.
- Changing CLI behavior or introducing a breaking API contract.

## Decisions

### 1. Extend the existing issue read model with child rows

Add a compact `IssueChildRef` read shape containing number, title, status, health, and persisted repository name. Add `Children` to `IssueReadModel`, ordered by issue number, and add `BlockedCount` to `ChildIssuesSummary`. `IssueQuerier.EnrichAsync` will load child rows for all parent numbers in the current result set in one project-scoped query, group them by parent, and derive both `Children` and `ChildIssuesSummary` from the same rows.

The projection will use `IssueRow` computed columns for identity, title, status, parent, and repository. Health is not currently a computed column, so the selected child state will be deserialized through the existing issue loader/mapping path to obtain canonical health. This keeps health interpretation in the server rather than duplicating it in SQL or TypeScript. Archived children follow the existing composite query semantics and are excluded from current child collections and counts.

For board list reads this adds compact child data only to parent issues; ordinary issues keep an empty collection. For detail reads the same contract supplies the complete child list. Existing `parentIssueRef`, repository resolution, and API envelopes remain additive and unchanged.

**Alternatives considered:**

- Add `GET /issues/{number}/children` and fetch it only on detail. Rejected because cards also need blocked counts and progress, it creates separate cache/invalidation behavior, and it allows summary and detail to drift.
- Keep only aggregate counts on list and add child rows only to detail. Rejected because two projection paths would own the same composite facts.
- Join children in the Web from the board's issue array. Rejected because detail routes may be opened directly, archived/filter state can make the list incomplete, and relationship assembly belongs to the Issue read model.
- Persist blocked-child count on the parent. Rejected because it duplicates child health authority and requires new event-driven consistency work for a display projection.

### 2. Keep repository filtering in board query state

Extend `BoardQueryState` with one optional repository name and serialize it as the `repository` query parameter. `applyBoardFilters` will compare the selected value with each issue's persisted `repository.name`/canonical repository name using the existing exact resource-name semantics. Filtering remains client-side and composes in the same pipeline as priority, labels, title search, and sorting.

`KanbanBoard` will receive declared repositories (or read them through the Project entity hook) to render one shared desktop/mobile filter control. An unknown URL value remains represented and yields no matches until cleared. With one repository, cards still show the repository label but the filter does not demand a choice.

`IssueCard` will render a compact repository label and, when `children.length > 0`, a `done/total` progress badge plus an independent blocked-child warning. These are separate from workflow status because a parent has no workflow stage and blocked-child attention is not the parent's own workflow health.

**Alternatives considered:**

- Use the existing server `repository` list query parameter for every filter change. Rejected because the board already has the complete project issue set and all other interactive filters are local; server refetches would add latency and split URL-filter semantics across layers.
- Encode repository as a synthetic issue label. Rejected because repository is a first-class Project resource and persisted Issue assignment, not user classification.
- Hide repository labels in single-repository projects. Rejected because the specs require every card to identify assignment; only the selection friction is removed.

### 3. Make parent detail a distinct presentation mode and disable workflow reads

`IssueDetailPage` will derive `isCompositeParent` solely from the server projection (`children.length > 0` or the equivalent `hasChildren` field). A dedicated parent overview section in the page slice will render progress, blocked count, and linked child rows using `useProjectPath`. The existing parent reference in `IssueDetailsCard` will become a real project-scoped `Link` for child pages. Repository metadata remains in the details rail for all issue kinds.

Workflow-only hooks will accept or receive an `enabled` guard and remain disabled for parents: timeline, diff, commits, sessions, workspace/rebase state, artifacts, and workflow profile reads. Workflow-oriented rendering, decision/recovery controls, and mobile action surfaces will be gated behind `!isCompositeParent`. Parent-valid issue-level actions, prerequisites, description, comments, and metadata remain rendered. The parent branch will not fabricate a workflow decision from absent stage data.

**Alternatives considered:**

- Create a second route/page for parent issues. Rejected because parent is a derived state of Issue, can disappear when children detach, and shares most issue detail content.
- Only hide workflow components while leaving their queries active. Rejected because it performs invalid or wasted workflow requests and leaves hidden components coupled to data that cannot exist for a parent.
- Put the child list in a reusable cross-page widget. Rejected for now because it is used only by the issue detail route; the page slice is the narrowest stable owner.

### 4. Treat New Issue selections as drafts; keep POST validation authoritative

Extend the `createIssue` input with `parentIssueNumber`. `CreateIssueDialog` will initialize repository state from the repository marked `isDefault` whenever repository data/current project changes; a single repository is submitted automatically, while multi-repository projects expose the selector.

The form will load the current project's active issue list through `entities/issue` and derive candidate parents for presentation. Candidates must not be terminal and must not have `parentIssueRef`; any other server-known eligibility indicator available in the read model is applied conservatively. This filtering improves the picker but is not an authorization or invariant boundary. The selected repository name and parent number are sent in the same existing POST.

The existing server route/coordinator remains unchanged as the write authority and continues returning stable error codes such as `repository_not_found`, `parent_not_found`, `parent_ineligible`, and `parent_is_sub_issue`. The feature maps those codes to assignment-specific messages. Mutation failure does not call `resetAndClose`, so all draft fields remain intact. Success invalidates project issue queries broadly enough to refresh the board, parent detail, and child candidate list.

**Alternatives considered:**

- Add a dedicated eligible-parent endpoint. Rejected because the form already needs issue identity/title data and eligibility can race immediately after any endpoint response; POST validation remains mandatory. A dedicated endpoint is warranted only if the active issue list becomes too large or the read model cannot express eligibility.
- Reimplement every domain invariant in the Web. Rejected because it will drift and still cannot remove races.
- Auto-select a parent based on navigation context. Rejected because the current New Issue entry point is global and implicit relationship creation is outside the requested behavior.

### 5. Test projections and behavior at their ownership boundaries

Server querier specs will verify one project-scoped child projection: deterministic order, status totals, blocked count, repository names, archived exclusion, detach behavior, and parent backlinks. Existing API specs receive one additive JSON-shape/success assertion; domain invariant matrices are not repeated through HTTP.

Web unit tests will cover board query parse/serialize/filter composition and card presentation. Component tests with MSW will cover repository/default selection, parent candidate filtering, combined create payload, error-code presentation with preserved drafts, parent detail child navigation, and suppression of workflow queries/surfaces. Existing desktop/mobile board regression tests will include repository filter reachability. Browser coverage is needed only if compact card/filter layouts cannot be proven without a real layout engine.

**Alternatives considered:**

- Assert every scenario through full API and page tests. Rejected because it duplicates lower-layer matrices and increases modification cost.
- Add real server/Web end-to-end tests. Rejected because the established test architecture uses in-memory server specs and MSW-backed Web tests without external dependencies.

## Risks / Trade-offs

- [Adding child rows to every board response increases payload and enrichment work for projects with many composite issues] -> Select only compact child fields, batch by returned parent numbers, avoid full child `IssueReadModel` enrichment, and add a query-count/payload-focused regression test if representative fixtures show material growth.
- [Health requires state deserialization because `IssueRow` has no health computed column] -> Deserialize only matched child rows through the canonical mapping path; do not add a schema column solely for this UI until profiling proves it necessary.
- [Client-side parent candidate filtering can become stale between selection and submit] -> Keep POST validation authoritative, preserve form drafts, map stable API codes to actionable messages, and refresh issue queries after errors where eligibility may have changed.
- [Gating a large detail page can accidentally leave a workflow request or control active for parents] -> Derive one `isCompositeParent` flag, pass it to every workflow query enablement and render boundary, and add an MSW test that fails on any unexpected parent workflow request.
- [Unknown repository URL values can look like data loss] -> Keep the selected value visible in the filter and provide a clear reset; do not silently fall back to all repositories.
- [Parent repository metadata may imply an execution location even though parents do not run workflows] -> Present it as persisted issue metadata and keep branch/diff/workflow surfaces absent.
- [Existing read projections may briefly lag after child creation or relationship changes] -> Use TanStack Query invalidation after successful mutations and rely on normal server reconciliation; do not add client-side optimistic composite totals.

## Migration Plan

1. Add the child read DTO, `Children`, and `BlockedCount`; update `IssueQuerier.EnrichAsync` and server querier/API tests. This is an additive wire change and requires no database migration or backfill because all source fields already exist.
2. Update Web Issue types and API client inputs. Add board repository state/filter controls and card presentation with unit coverage.
3. Add composite parent detail rendering, parent backlinks, and workflow query/render guards with MSW-backed page tests.
4. Add New Issue default repository initialization, parent selection, combined submission, error mapping, and query invalidation with component tests.
5. Run server tests plus Web typecheck and tests; run browser layout tests only if the responsive filter/card changes require them.

Deployment can use the normal server-before-Web order: the old Web ignores additive fields, while the new Web expects the updated server projection. Rollback the Web first, then server; persisted Issue and Project data are unchanged, so no data rollback is required.

## Open Questions

- None. The existing domain and product specs settle child inclusion, progress semantics, repository identity, parent eligibility authority, and parent workflow suppression. Implementation should raise a follow-up only if measured board payload growth requires a paged or separate read surface.
