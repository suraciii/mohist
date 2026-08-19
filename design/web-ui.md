# Web UI

Mohist's fallback operations and visualization plane presents authoritative
execution state, evidence, relationships, and safe actions when the owner needs
a global view or manual takeover.

## Product Boundary

- The primary conversation normally remains in Slack, an IDE, or another
  external surface. Web does not recreate those products. It does provide a
  complete direct Mohist Agent client for configuration, launch, Follow-up, Job
  result, Session evidence, and recovery.
- Fallback means infrequent, not incomplete. Critical lifecycle, Approval,
  recovery, and configuration actions remain available without an External
  Agent.
- Web emphasizes relationships and evidence that a short Agent summary cannot
  show well: Project attention, Issue and Epic progress, Workflow state, diffs,
  AgentSession transcript, and system health.
- A new domain action cannot exist only in Web. Web submits the same
  Server-owned intent available to other clients and never interprets Workflow
  state separately.
- Web, CLI, and Slack adapter consume the same Agent API. Web cannot add
  launch-time Agent configuration overrides because it owns the editor.

## Ownership

Web UI owns render state and sends user actions to the API. The Server owns
authoritative state; Workflow decisions belong to the Workflow context on the
Server. The Runner owns Shell, Agent, and Git execution. Real-time push flows
from the Server to the Web UI. The Agent context owns the Agent definition and
the AgentJob result through the Agent API, and Agent Connection binding and
policy through the API.

Web UI never interprets Workflow rules. It renders Server state and submits
user intent.

## Events

Push is observation, not a driver: a Workflow decision commits on the Server,
publishes over SignalR on `/hubs/events`, and refreshes the affected queries.
After reconnect, the UI reconciles its queries.

## Routes

UI and API use domain-identity paths:
`/projects/{projectId}/issues/{issueNumber}` and
`/projects/{projectId}/epics/{epicNumber}`. Issue and Epic numbers no longer
resolve to separate internal IDs. WorkflowRun continues to use `workflowRunId`.

## Rules

- Query hooks own data retrieval and cache invalidation.
- UI state stores view preferences, filters, and drafts, never Workflow truth.
- Runner details remain behind the API. UI does not depend on process internals.

## Agent Product Surface

Agent list and detail form a management and test surface, not a decorative
catalog. They expose definition, launch, separate Job and Session state, and
Connections without requiring inference from raw transcript events.

Identity, lifecycle, configuration Readiness, execution availability, and
Connection health are separate signals. UI does not turn an offline Runner into
an Agent configuration error or combine Slack health and Agent Readiness in one
badge. Missing configuration links to the Agent edit location. `unknown`
remains distinct from `ready` and `needs-setup`.

Direct launch uses the same Agent API request as CLI and Slack, except for
authenticated actor and source metadata. Agent fields are edited before launch.
The composer accepts only Prompt, context references, and attachments. Runtime,
Model, and Skills overrides do not belong in the composer.

AgentSession renders two modes from the same Session model:

- A Workflow origin emphasizes task ownership, evidence, and recovery.
- An Agent launch origin also provides a complete Follow-up composer as the
  fallback direct conversation client.

See [`session-timeline.md`](session-timeline.md) for timeline sentence form,
domain-action recognition, collapse and salience rules, and raw view.

Route mode cannot change Session lifecycle or API. AgentJob result is separate
from Session Activity and AgentTurn progress. Connection setup and health
belong on Agent details because the user starts from the Agent to expose.

The Connection panel shows resumable setup, next action, access policy, identity
alignment, and health. Allowlist editing uses member names and avatars for the
human control. Display name is never authorization identity, and Web never
reads Slack tokens.

## Frontend Module Boundary

The Web application uses Feature-Sliced Design with `app`, `pages`, `widgets`,
`features`, `entities`, and `shared`. Dependencies point from higher to lower
layers. Slices in one layer do not depend directly on each other. When entities
have a real model relationship, declare a narrow contract through
`entities/<entity>/@x`.

- `app` owns startup, Providers, and route composition. It consumes route pages
  and the application shell through page or widget `index.ts`, not internal
  `ui` or `model` files.
- `pages` owns interaction and state valid only within one route. Settings
  search depends on Settings route, tab, and focus target, so it belongs to
  `pages/settings` instead of a reusable Feature.
- `shared` owns browser capability without business ownership. It provides
  Theme context and keyboard-shortcut declarations and registry. `app` mounts
  ThemeProvider. Pages and common components consume the shared API.
- Static filter values used by several domain APIs belong to `shared/config`.
  Missing-resource presentation belongs to `shared/ui`. A route page only
  places these common capabilities at the route entry.
- A slice exports only stable pages, components, or domain contracts. Internal
  `ui`, `model`, and `api` paths cannot be cross-slice import targets.

## Presentation Preference

Use dense, scannable screens. Do not use a landing page or chat-first
application composition. A direct AgentSession can use a conversation layout
because conversation is the task on that route, but it is not the application
home.

Order first screens by attention-first production overview, Issue execution
details, Approval and recovery, execution evidence, and Runner state.
