## Why

The issue detail page repeats workflow tasks, diff state, and artifacts while hiding the identity of completed tasks and activity subjects and leaving comments without truthful attribution. Consolidating each fact into one clear, addressable place restores a reliable reading flow on desktop and phone-width screens.

## What Changes

- Replace the separate workflow step list and task progress panel with one stage-aware task list; task names remain primary and visible at every viewport width, artifact paths are secondary, and only rows that reveal logs or other detail use interactive styling.
- Make every workflow stage selectable and reachable on phone-width screens so its tasks can be inspected without relying on hidden horizontal overflow.
- Merge the inline diff summary, changed-files summary, and duplicated unavailable messages into one Changes section with one consequence-oriented degraded state.
- Replace the compact and full artifact placements with one Artifacts section at a stable point in the reading flow, without adding approval-time inline artifact reading.
- Separate issue-body frontmatter from description content in rendering, previews, and editing. Present recommendation fields as recommendations, keep current Issue risk and workflow profile authoritative when body defaults disagree, and keep malformed or unclosed leading envelopes out of the reading flow.
- Give workflow, artifacts, activity, and comments stable URL anchors, including a URL-addressable Activity view that opens or scrolls to the requested content.
- Make activity entries identify the task or artifact they concern. New comments require a declared author that is validated, persisted, and returned with the comment; historical comments created before attribution show `Unknown author`.
- **BREAKING** Comment creation through Web, API, and CLI now requires a nonblank author label because the current product has no authenticated user identity to derive one from.

## Capabilities

- `issue-detail-reading-flow`: The single, attention-ordered issue reading flow, including one stage-aware task list, one Changes section, one Artifacts section, honest task-row affordances, persistent task names, and responsive access to every workflow stage.
- `issue-metadata-presentation`: Separation of structured issue frontmatter from description content across metadata display, rendered and collapsed descriptions, and issue editing, including authoritative-field precedence and malformed-envelope behavior.
- `issue-detail-navigation`: Stable URL addressing and landing behavior for workflow, artifacts, activity, and comments on the issue detail page.
- `issue-history-attribution`: Human-readable attribution in issue history, including task and artifact subjects in Activity, required declared authorship for new comments, and explicit unknown attribution for historical comments.

## Impact

- **Web issue detail composition** (`packages/web/src/pages/issue-detail`): reading-flow ordering, section ownership, degraded diff state, anchors, description preview, Details metadata, comments, and edit-dialog behavior change.
- **Workflow and activity widgets** (`packages/web/src/widgets/issue-workflow`, `packages/web/src/widgets/issue-event-timeline`): duplicate task and artifact presentations converge; stage selection, task-row behavior, responsive layout, and event descriptions change while continuing to consume the existing workflow timeline and artifact queries.
- **Issue comment contract** (`packages/server` Issue API/domain/persistence, `packages/web/src/entities/issue`, and `packages/cli`): new comments carry a required declared author end to end; an additive nullable persisted/read field preserves historical rows as unknown.
- **Routing and links**: issue detail URLs gain section fragments without changing the existing issue route or changed-files route.
- **Dependencies and systems**: one database migration adds nullable comment author storage; no new external dependency, workflow behavior, runner behavior, or artifact-content change is expected. Inline approval evidence and decision-action convergence remain owned by their sibling issues.
