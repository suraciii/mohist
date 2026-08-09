# Low-bandwidth Issue List Reads and Request Isolation

The Issue list is a Project-scoped summary read model for boards, archived
lists, CLI lists, and a small number of aggregate reads. `IssueReadModel` from
`GET /issues/{number}` remains responsible for Issue details.

## Design Drivers

- **List cost follows list size:** Comments, attachments, history, and merged
  Variables can grow independently of the number of Issues. A board read must
  not pay those costs for every row.
- **Invalidation follows resource identity:** One event should refresh only the
  Project, Issue, Workflow, artifact, or Inbox resource it can change. A broad
  cache prefix turns unrelated activity into repeated reads.
- **Cold transfer follows the current route:** Opening the shell should not load
  every page, dialog, or candidate collection. The user pays for a route or
  optional workflow only when entering it.

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

### Collection Cost Boundary

`GET /issues` projects current Project-scoped Issue state and joins only the
current Workflow and compact relationship facts required by `IssueListItem`.
Workflow, parent, child, prerequisite, and Epic facts are read in batches.
Filtering and sorting operate on that bounded projection.

The list path never reads comments, attachments, feedback, history, or merged
Variable layers. Detail and independent subresource reads own those facts.
Therefore list work grows with the number of returned Issues and their compact
relationships, not with unrelated detail volume, and it performs no per-Issue
database or HTTP query.

### HTTP Endpoints

Add under `/api/projects/{projectRef}/issues`:

```text literal
GET /parent-candidates
  data: [{ number, title }]
```

Add under `/api/projects/{projectRef}/inbox`:

```text literal
GET /unread-count
  data: { unreadCount }
```

Both endpoints use the existing Project resolution filter. The filter still
returns 404 for an unknown Project; the SPA fallback cannot turn it into a
successful response.

### Request and Cache Isolation

Web uses separate key factories rather than one `['issues']` prefix for every
resource:

```text literal
issue-list       project + list filters
issue-detail     project + issue number
issue-workflow   project + issue number + workflow subresource
issue-artifacts  project + issue number + artifact subresource
issue-candidates project
inbox-list       project
inbox-count      project
```

Invalidation for detail, Workflow, artifact, candidate, and Inbox list or count
matches only its own namespace. Every read carries the caller's cancellation
signal through the shared request boundary so leaving a route can stop work that
no longer has a consumer.

The Create Issue dialog does not exist in the active component tree while
closed, so it cannot request candidates. Opening it reads Project-scoped parent
candidates. The prerequisite picker likewise loads compact summaries only after
the user expands it. A successful create invalidates the affected Project list
and candidates plus only the details and relationships of an affected parent.

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

Invalidating an inactive list marks it stale without forcing a network request.
Detail keys match the Issue number exactly. An event for Issue #474 therefore
does not request Issue #473, and an unrelated event burst that does not change
list structure does not refresh the collection. Every reread uses HTTP truth;
an event is an invalidation hint and never becomes synthesized list state.

### Cold Transfer and Static Serving

The production shell contains only startup providers and route selection. Page
modules and the Create Issue dialog load on demand, so a static dependency from
the shell cannot pull an unused route into the cold entry. Production assets are
minified, omit source maps, and use Brotli or gzip when supported.

Static-file cache boundaries are:

- `/assets/*`: `Cache-Control: public,max-age=31536000,immutable`.
- `index.html` and SPA fallback: `Cache-Control: no-cache`.
- `/api/*` and `/otel/v1/*` do not enter the SPA fallback, and unknown paths
  return 404.

Build checks enforce fingerprinted assets, no source maps, independent route
chunks, and compressed size budgets. Static-serving checks protect cache headers,
HTML fallback, and API 404 behavior. These are observable transfer contracts,
not a required bundler implementation.

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

The low-bandwidth list, isolated query namespaces, request cancellation, lazy
route transfer, compression, and cache boundaries are implemented. A later
list-field or event-contract extension must preserve the three design drivers
and update the Server/Web contract and behavioral checks together.
