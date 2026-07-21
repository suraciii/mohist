## Context

The issue detail route currently composes several independently evolved presentations of the same read data. `WorkflowView` renders stage tasks from the workflow timeline, while `TaskProgressPanel` maps the same timeline again to provide progress and logs. The page also renders diff facts in an inline banner and `IssueDiffFilesSection`, places workspace-unavailable copy after both diff and commit sections, and retains both compact and full modes in `ArtifactOpener` even though approval-specific evidence now has its own presentation.

Inspection is coupled to mutation policy: `IssueDetailPage` passes `readOnly` to `WorkflowView`, which prevents stage selection and task expansion as well as workflow changes. On narrow screens the four fixed stages use a horizontally scrolling row, so later stages are not visibly reachable. Description rendering and editing consume `issue.body` verbatim; the only frontmatter parser lives inside `features/create-issue`, where the detail page and sibling edit feature cannot reuse it without violating slice boundaries. Activity events carry stable task IDs and artifact paths, but the formatter does not resolve those subjects. Comments expose neither author in the Web model nor author in the server read DTO.

The change is primarily an issue-detail Web restructuring. It must preserve workflow authority in the server, existing timeline/artifact/diff APIs, approval-specific inline evidence, and the current issue route. React Router 7 location state is the authority for section fragments; URL state is not duplicated into independent component state.

## Goals / Non-Goals

**Goals:**

- Give workflow tasks, changed-file state, and the ordinary artifact collection one presentation owner each.
- Keep task names visible and make inspection interactive independently of whether workflow mutation is allowed.
- Partition issue frontmatter from description content once and reuse that result in create, detail, metadata, preview, and edit flows.
- Make `#workflow`, `#artifacts`, `#activity`, and `#comments` reliable after asynchronous route data renders.
- Resolve task and artifact subjects in Activity without changing durable workflow event contracts.
- Show truthful comment attribution under Mohist's current single-operator identity model.

**Non-Goals:**

- Changing workflow state transitions, polling/live-update behavior, task or artifact persistence, artifact content rendering, or the changed-files reader route.
- Reworking approval-package evidence or decision actions delivered by sibling issues.
- Introducing user accounts, per-person authentication, or a general actor/audit model.
- Parsing arbitrary YAML frontmatter or changing the issue-body frontmatter language.

## Decisions

### 1. Make `WorkflowView` the sole owner of task inspection

`WorkflowView` will continue to fetch and map the workflow timeline once, own `selectedStage`, and render the only task list through `StepList`. The canonical `TaskItem` will absorb the useful inspection behavior currently isolated in `TaskProgressPanel`, especially `TaskLogPanel`, structured output, failure guidance, required files, artifact summaries, and session links. `TaskProgressPanel` and its second timeline mapping will be removed from the issue page and then deleted with superseded tests/exports.

Inspection capability will be separate from mutation capability. Archived, done, and page-level read-only issues may still select stages, open task logs, inspect output, and open artifacts; only controls that mutate workflow state remain disabled or absent. A task row with expandable content will use a semantic disclosure control. A task with no expandable content will render a non-button row. Secondary links and artifact actions will be siblings of the disclosure control rather than nested interactive elements. The title receives the flexible, wrapping primary slot; metadata and artifact paths use a secondary row so they cannot replace the title at narrow widths.

Alternative considered: keep `TaskProgressPanel` and hide `StepList` tasks. Rejected because checks, approval evidence, feedback history, required files, and artifact summaries already belong to `StepList`; moving all of that would invert ownership and preserve two timeline mappers.

### 2. Render the four stages as a responsive grid

The fixed Plan/Build/Check/Integrate selector will use four columns where space permits and a two-column grid at phone width. Every stage control remains enabled for inspection, including completed and pending stages. Selection uses `aria-current` or the equivalent selected-state semantics; mutation availability does not affect stage navigation. Connector arrows and horizontal overflow are removed at narrow width because they obscure reachability without adding workflow meaning.

Alternative considered: retain horizontal scrolling and add fades or scroll buttons. Rejected because four stable stages fit predictably in a 2x2 grid, which exposes the complete workflow with fewer controls and no hidden state.

### 3. Consolidate page-level Changes and Artifacts ownership

`IssueDiffFilesSection` will become the one Changes section and receive the diff query state needed to render available, loading, empty, and unavailable states. The separate diff banner and trailing concatenated diff/commit fallback are removed. When the workspace prevents both diff and commit inspection, the Changes message states both consequences once; `IssueCommitsSection` remains a separate section only when commit data is available and does not repeat the shared workspace error.

`LatestArtifactsPanel` remains the ordinary reading-flow artifact owner and receives the `id="artifacts"` section boundary. `ArtifactOpener` will collapse to one full-list behavior; the unused compact mode, compact-only API, and duplicate evidence markup are removed. Approval packages continue to render their required inline evidence directly and do not reintroduce the ordinary artifact collection.

Alternative considered: extract shared summary primitives and render them in multiple locations. Rejected because the requirement is one fact in one place; reusable formatting would make duplication easier rather than establish ownership.

### 4. Move issue-body partitioning into `entities/issue`

The current frontmatter parser will move from `features/create-issue` to the Issue entity public API and become the single authority for partitioning an issue body into:

- recognized metadata (`recommendedWorkflow`, `recommendedWorkflowReason`, `risk`),
- description content after the leading envelope, and
- the original raw envelope needed for lossless description-only edits.

The parser remains a small deterministic parser for the existing three keys, quoted scalars, and literal/folded blocks; no YAML dependency is added. A bounded leading envelope is removed from reading and editing even when recognized fields are malformed, while its raw text is retained. Create uses the recognized metadata as it does today. Detail parses once and passes description content to `IssueDescriptionSection` and metadata to `IssueDetailsCard`. Preview generation therefore runs only on description content. Edit initializes its textarea from description content and recombines the untouched raw envelope with the edited description before PATCH, preserving recognized and unknown envelope lines byte-for-byte. Attachment extraction runs on the recombined body.

Alternative considered: strip frontmatter independently in the description, preview, and editor with regular expressions. Rejected because delimiter, block-scalar, CRLF, BOM, malformed-envelope, and preservation rules would diverge across consumers. Alternative considered: promote the fields into new persisted Issue columns. Rejected because this change is presentation separation, and workflow profile/risk already have authoritative fields outside this legacy recommendation envelope; a persistence migration would create competing sources.

### 5. Derive section behavior from the React Router location

`IssueDetailPage` will own a route-local section-navigation helper in its `model` segment. It reads `location.hash` with `useLocation`, observes the rendered-content readiness needed by each target, and calls `scrollIntoView` for `workflow`, `artifacts`, and `comments`. Stable DOM IDs provide native anchor semantics; the effect covers the asynchronous-render case where the browser attempts fragment scrolling before query-backed sections exist.

Activity remains a dialog but becomes controlled when composed by the issue page. `#activity` is the source of truth for its open state; the ordinary Activity trigger navigates to the same pathname/search with that hash, and closing clears the hash without changing project or issue identity. `ActivityDialog` accepts controlled `open`/`onOpenChange` props while retaining an uncontrolled default for other consumers. No parallel `activityOpen` page state is introduced.

Alternative considered: add nested routes for each section. Rejected because these are destinations within one document, not independent resources, and nested routes would duplicate loading and route composition. Alternative considered: rely only on browser-native anchor scrolling. Rejected because workflow and artifact sections render after queries resolve and Activity has no inline scroll target.

### 6. Resolve Activity subjects in the Web read presentation

Durable task events remain identity-based (`stage` plus `taskId`), and artifact events continue to carry `path`. The activity widget will obtain the existing workflow timeline when enabled and build a `(stage, taskId) -> title` lookup. `describeEvent` receives this resolver and formats started/completed/failed task summaries with the title, falling back to the stable task ID when the timeline is unavailable or an old task cannot be resolved. Artifact-recorded summaries use the event path directly. Historical and live entries pass through the same formatter, so they cannot diverge.

Alternative considered: add task titles to domain events. Rejected because titles are display data already available in the workflow read model; changing immutable event payloads would duplicate facts and still leave historical events unenriched. Alternative considered: enrich events in `WorkflowEventQuerier`. Rejected because this is presentation assembly local to one Web widget and does not justify changing the generic stored-event API.

### 7. Expose role-level comment attribution without inventing users

The current API has one authenticated operator principal and no person identity. `IssueCommentDto` will therefore expose an additive `author` value of `Operator` from the read projection, and the Web `Comment` model will accept `author`. `IssueCommentsSection` renders that value beside the timestamp and uses `Unknown author` only when an older or test response omits it. This requires no new column or migration because the server cannot currently record a more specific truthful identity.

Alternative considered: persist a caller-supplied author string. Rejected because it is spoofable and there is no authenticated person to validate it against. Alternative considered: display `You` entirely in the client. Rejected because CLI and automated callers also use the operator boundary, so a role label is more accurate. A future multi-user identity design must replace the derived role with an authenticated actor, rather than reinterpret this label as a person.

## Risks / Trade-offs

- [Merging task rows drops logs, failure guidance, checks, or approval history] -> Keep `StepList` as the composition owner, move `TaskLogPanel` into the canonical row first, and delete `TaskProgressPanel` only after focused behavior coverage is equivalent.
- [Selected stage resets when timeline polling updates] -> Reset selection only when the issue identity or authoritative current-stage default changes, not for every timeline object refresh.
- [The 2x2 mobile stage grid makes labels or durations overflow] -> Use fixed grid tracks, wrapping labels, and browser coverage at the longest supported labels and phone viewport.
- [A fragment arrives before its query-backed section exists] -> Re-run the route-local reveal effect when issue/workflow/artifact readiness changes; keep stable IDs on persistent section boundaries.
- [Opening or closing Activity creates URL/dialog feedback loops] -> Treat the hash as the only controlled state and make callbacks perform idempotent hash navigation.
- [Frontmatter edits lose unknown keys or line endings] -> Preserve the original envelope verbatim and replace only the post-envelope description; lock BOM, CRLF, block scalar, malformed, empty-description, and unknown-key cases with entity tests.
- [Task events from invalidated or old attempts cannot resolve a title] -> Use `(stage, taskId)` lookup and visibly fall back to task ID; never return an anonymous `Task Started` label.
- [Operator is less specific than a person's name] -> Present it explicitly as a role and avoid persistence until authenticated user identity exists.

## Migration Plan

1. Move and extend the frontmatter parser under `entities/issue`, update create-issue imports through the entity public API, and add partition/recomposition tests before changing consumers.
2. Merge log inspection into the canonical workflow task row, separate inspection from mutation state, make the stage selector responsive, remove `TaskProgressPanel`, and update workflow widget tests.
3. Consolidate Changes and Artifacts sections and remove duplicate page composition and the unused compact artifact mode.
4. Add stable section IDs and URL-derived Activity control, then cover direct fragments, in-page hash changes, and asynchronous targets in issue-detail specs.
5. Add activity subject resolution and the additive comment author read field, then update formatter, API projection, and comment rendering tests.
6. Run Web FSD checks, typecheck, and focused unit/spec suites. Use browser tests for real phone-width stage reachability, title visibility, non-overlap, and fragment landing because those depend on layout and scrolling.

No persisted-data migration is required. The additive comment DTO field permits server-first or Web-first deployment because the Web fallback handles its absence. Rollback restores the prior Web composition and ignores the additive DTO field; no data rollback is needed.

## Open Questions

- When Mohist gains authenticated user identities, define whether existing role-attributed comments remain `Operator` or are migrated only when a trustworthy actor mapping exists. This does not block the current single-operator implementation.
