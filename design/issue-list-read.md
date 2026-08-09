# Low-bandwidth Issue List Reads and Request Isolation

The Issue list is a Project-scoped summary read model for boards, archived
lists, CLI lists, and a small number of aggregate reads. `IssueReadModel` from
`GET /issues/{number}` remains responsible for Issue details.

## Model

### IssueListItem

`IssueListItem` is the shared Server, Web, and CLI list contract. It contains
only current state, actionability, and compact relationship summaries used by
lists and boards:

- `number`, `title`, `status`, `health`, `projectId`, and `projectName`.
- `labels`, `priority`, `risk`, `createdAt`, `updatedAt`, `archivedAt`, and
  `completedAt`.
- `approvalState`, `blockedReason`, `workflowRunId`, `workflowStage`,
  `workflowStatus`, `workflowStageProgress`, and `workflowProfileId`.
- `prerequisiteNumbers`, `prerequisites`, `isDraft`, `canStart`, `canBeParent`,
  and `blocker`.
- `repositoryName`, `repository`, `repositoryProblem`, `primaryEpic`,
  `parentIssueRef`, `childIssuesSummary`, and the existing compact `children`
  references.

The list contract excludes `body`, `comments`, `attachments`, `feedback`,
`agentConfig`, `model`, `modelVariant`, `stageModels`, `stageModelVariants`, and
configuration merged from each Issue's Variable layers. `workflowProfileId`
identifies the currently effective Profile; it is not an expanded Variable
result.

An `IssueListItem` is identified by `(projectId, number)`. Existing Project,
status, label, priority, repository, parent, and archived or all filters for
`GET /issues` do not change. Results remain sorted by ascending `number`.
`GET /issues/{number}` does not become a summary and still returns 404 for a
missing Issue.

### ParentCandidate

`ParentCandidate` contains only `number` and `title`. It belongs to one Project
and includes only an unarchived backlog Issue whose Workflow has not started
and which has no parent. Server returns candidates by ascending number. Web no
longer derives them from a complete Issue list.

### InboxUnreadCount

`InboxUnreadCount` contains only `unreadCount`. It counts unarchived Inbox rows
with null `ReadAt` in the current Project. Only the Inbox page uses the complete
Inbox read model.

## Semantics

### Collection Assembly

`GET /issues` builds `IssueListItem` from current Project-scoped `IssueRow`
state and batched relationship reads:

1. Read Issue state columns and required current-state fields for the Project.
2. Batch-read current Workflow state for list Stage, health, Approval, and
   progress.
3. Batch-read parent, child, prerequisite, and Epic relationships for compact
   relationship fields.
4. Apply request filters and sorting, then serialize `IssueListItem`.

The list path cannot call `EnrichAsync` or read Issue comments, Issue or comment
attachments, Issue Workflow Variables, global or Project Variables, or Workflow
feedback and history. Comments, attachments, feedback, and Variables are read
only by details or their independent APIs. List cost therefore cannot grow with
unrelated comment, attachment, or history counts. Relationship reads must be
batched and cannot produce per-Issue `AnyAsync` or HTTP queries.

### HTTP Endpoints

Add under `/api/projects/{projectRef}/issues`:

```text
GET /parent-candidates
  data: [{ number, title }]
```

Add under `/api/projects/{projectRef}/inbox`:

```text
GET /unread-count
  data: { unreadCount }
```

Both endpoints use the existing Project resolution filter. The filter still
returns 404 for an unknown Project; the SPA fallback cannot turn it into a
successful response.

### Web Query Namespaces

Web uses separate key factories rather than one `['issues']` prefix for every
resource:

```text
issue-list       project + list filters
issue-detail     project + issue number
issue-workflow   project + issue number + workflow subresource
issue-artifacts  project + issue number + artifact subresource
issue-candidates project
inbox-list       project
inbox-count      project
```

Invalidation for detail, Workflow, artifact, candidate, and Inbox list or count
must match only its own namespace. Every TanStack Query `queryFn` passes
`context.signal` to the API client, which passes it through the shared
request's `RequestInit.signal` to `fetch`.

The `open` condition controls mounting of the Create Issue dialog. When closed,
there is no dialog component, candidate query, or full Issue-list query. When
opened, it requests Project-scoped `parent-candidates` once under normal Query
cache semantics and preserves existing candidate selection and invalidation
cleanup. The prerequisite picker reads no Issue list before expansion; after
expansion, it loads searchable compact summaries on demand. Successful create
invalidates only the affected Project's active list and candidates plus details
and relationships for an affected parent.

The Inbox shell badge uses `unread-count`; the Inbox page continues to use
complete `/inbox`. Read, read-all, and archive operations invalidate both
`inbox-list` and `inbox-count`, so HTTP truth reconciles the badge and page. A
real-time hint does not synthesize an Inbox item.

### Event-to-resource Invalidation

`projectId` and `issueNumber` from the event envelope route invalidation. An
event without the current Project ID does not touch its cache; a different
Project ID is ignored. An event with an Issue number invalidates only detail,
Workflow, and artifact keys for `(projectId, issueNumber)`, never a broad key
unrelated to the Issue number.

- Issue create, archive, cancel, reopen, start, complete, draft, label,
  priority, prerequisite, Workflow Profile, repository, Epic, parent, and
  composite-state changes invalidate the Project's active list. Create, parent,
  and candidate-eligibility changes also invalidate `issue-candidates`. A
  parent change precisely invalidates previous and current parent detail; other
  events invalidate only the event Issue's detail and affected relationships.
- Workflow Run, Stage, Approval, and Task events invalidate the event Issue's
  detail and Workflow plus the Project's active list. An artifact event
  invalidates only its artifact and Workflow resources.
- AgentSession events invalidate only corresponding Session, Workflow, detail,
  and existing Agent activity or status resources.
- An Inbox hint invalidates only `inbox-list` and `inbox-count` for the current
  Project.

Invalidating a list Project key uses TanStack Query's default active-refetch
semantics. An inactive list becomes stale without a network request. Detail keys
match the Issue number exactly. An event for Issue #474 therefore does not
request the currently viewed Issue #473 detail, and an unrelated event burst
that does not affect list structure does not request the current detail or
collection. Every reread uses the HTTP endpoint as truth and does not synthesize
list or detail state from an event payload.

### Cold Transfer and Static Serving

The Vite production build enables minification and disables source maps. `App`
keeps static entries for the shell, providers, and route table, but every page
route uses `React.lazy` under one `Suspense` boundary. The Create Issue dialog
also uses a lazy import. A static import in App cannot pull a page module into
the cold entry.

Server registers Brotli and gzip response compression for static JS, CSS, and
JSON when supported by the client. Static-file rules are:

- `/assets/*`: `Cache-Control: public,max-age=31536000,immutable`.
- `index.html` and SPA fallback: `Cache-Control: no-cache`.
- `/api/*` and `/otel/v1/*` do not enter the SPA fallback, and unknown paths
  return 404.

Production build assertions verify that every HTML-referenced asset is a
fingerprinted static file, no source map exists, independent route chunks
exist, and initial and route-chunk compressed budgets are printed and enforced.
Server static-serving specs verify headers and status for assets, HTML fallback,
and API 404 responses.

## Examples

### List Response Boundary

Given an Issue with 20 comments, 12 attachments, several feedback and history
records, and Workflow Variables, `GET /issues` still returns only
`IssueListItem`. Those counts do not change collection-assembly work.
`GET /issues/{number}` still returns body, comments, attachments, feedback, and
other fields required by details.

### Event Isolation

The current Project is viewing `473`, with detail key `('project', 473)` in the
cache. A Workflow event carrying `projectId = project` and `issueNumber = 474`
can mark only detail and Workflow keys for `474`, plus an active list explicitly
required by that event. It cannot match the detail key for `473`. An event with
the same Issue number in another Project touches no Issue key in the current
Project.

### Closed Dialog

On the first shell render with `createIssueOpen = false`, the Network view has
no `GET /issues` or `GET /parent-candidates`. Opening the dialog sends one
`GET /parent-candidates`; every response object contains only `number` and
`title`.

## Status

This spec is the implementation target for Issue #473's low-bandwidth
optimization and is delivered by the code and tests in this workspace. A later
list-field or event-contract extension must update this spec, the corresponding
Server and Web types, and behavioral specs together.
