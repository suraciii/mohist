# Low-bandwidth Issue List Reads and Request Isolation

The Issue list is a Project-scoped summary read model for boards, archived
lists, CLI lists, and small aggregate reads. `IssueReadModel` from
`GET /issues/{number}` remains the detail contract.

## Design Drivers

- List cost follows returned Issues, not comments, attachments, history, or
  merged Variables.
- Invalidation follows the identity of the resource an event can change.
- Cold transfer follows the current route. Optional pages and dialogs load only
  when used.

## Model

### IssueListItem

`IssueListItem` is the shared Server, Web, and CLI list contract. It contains
current state, actionability, and compact relationship summaries. It excludes
body, comments, attachments, feedback, Agent configuration, and merged
Variable layers. Detail and subresource reads own those facts.

The identity is `(projectId, number)`. Existing Project, status, label, priority,
Repository, parent, archived, and all filters for `GET /issues` remain. Results
sort by ascending `number`. `GET /issues/{number}` remains a detail read and
returns 404 for a missing Issue.

### ParentCandidate

`ParentCandidate` contains `number` and `title`. It belongs to one Project and
includes only an unarchived Backlog Issue whose Workflow has not started and
which has no parent. Server returns candidates in ascending number order. Web
must not derive candidates from a complete Issue list.

### InboxUnreadCount

`InboxUnreadCount` contains only `unreadCount`. It counts unarchived Inbox rows
with null `ReadAt` in the current Project. Only the Inbox page reads the full
Inbox model.

## Semantics

### Collection Cost Boundary

`GET /issues` projects current Project-scoped Issue state and joins only current
Workflow and compact relationship facts needed by `IssueListItem`. Workflow,
parent, child, prerequisite, and Epic facts are read in batches. Filtering and
sorting use that bounded projection.

The list path never reads comments, attachments, feedback, history, or merged
Variable layers. Work therefore grows with returned Issues and compact
relationships. It performs no per-Issue database or HTTP query.

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

Both endpoints use the existing Project resolution filter. An unknown Project
returns 404. SPA fallback must not turn that response into success.

### Request and Cache Isolation

Web uses separate key factories. It must not use one `['issues']` prefix for
all resources:

```text literal
issue-list       project + list filters
issue-detail     project + issue number
issue-workflow   project + issue number + workflow subresource
issue-artifacts  project + issue number + artifact subresource
issue-candidates project
inbox-list       project
inbox-count      project
```

Invalidation for detail, Workflow, artifact, candidates, Inbox list, and Inbox
count matches its own namespace. Every read carries the caller cancellation
signal through the shared request boundary. Leaving a route can stop work with
no consumer.

The closed Create Issue dialog is absent from the active component tree and
cannot request candidates. Opening it reads Project-scoped candidates. The
prerequisite picker loads compact summaries only when expanded. Successful
creation invalidates the affected Project list and candidates, plus only the
details and relationships of an affected parent.

The Inbox shell badge uses `unread-count`; the Inbox page uses complete
`/inbox`. Read, read-all, and archive invalidate both `inbox-list` and
`inbox-count`. A real-time hint never synthesizes an Inbox item.

### Event-to-resource Invalidation

`projectId` and `issueNumber` in the event envelope route invalidation. An event
without the current Project ID does not touch its cache. A different Project ID
is ignored. An event with an Issue number invalidates only detail, Workflow, and
artifact keys for `(projectId, issueNumber)`, plus an active list when the event
can change list structure. It never invalidates a broad unrelated key.

Invalidating an inactive list marks it stale without forcing a request. Detail
keys match Issue number exactly. An event for Issue #474 cannot request Issue
#473. Every reread uses HTTP truth. Events are invalidation hints, not
synthesized list state.

### Cold Transfer and Static Serving

The production shell contains startup providers and route selection only. Page
modules and the Create Issue dialog load on demand. A static shell dependency
must not pull an unused route into the cold entry. Production assets are
minified, omit source maps, and use Brotli or gzip when supported.

Static-file cache boundaries are:

- `/assets/*`: `Cache-Control: public,max-age=31536000,immutable`.
- `index.html` and SPA fallback: `Cache-Control: no-cache`.
- `/api/*` and `/otel/v1/*` bypass SPA fallback. Unknown paths return 404.

Fingerprinted assets, source-map absence, route chunks, compressed-size
budgets, cache headers, HTML fallback, and API 404 behavior are observable
transfer contracts. They do not require a specific bundler.

## Examples

### List Response Boundary

Given an Issue with 20 comments, 12 attachments, several feedback and history
records, and Workflow Variables, `GET /issues` still returns only
`IssueListItem`. Those counts do not change collection assembly work.
`GET /issues/{number}` still returns body, comments, attachments, feedback, and
other detail fields.

### Event Isolation

The current Project is viewing Issue `473`, with detail key
`('project', 473)` in cache. A Workflow event carrying `projectId = project` and
`issueNumber = 474` can mark only detail and Workflow keys for `474`, plus an
active list explicitly required by that event. It cannot match the detail key
for `473`. The same Issue number in another Project touches no current-Project
Issue key.

## Status

Low-bandwidth lists, isolated query namespaces, request cancellation, lazy
route transfer, compression, and cache boundaries are implemented. A future
list-field or event-contract extension must preserve the three design drivers
and update Server, Web, and behavioral checks together.
