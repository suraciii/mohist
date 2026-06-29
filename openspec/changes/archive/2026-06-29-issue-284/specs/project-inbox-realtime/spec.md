## ADDED Requirements

### Requirement: Server emits a project-scoped realtime hint strictly after an inbox item is persisted

After a successful, non-duplicate inbox item insert, the server SHALL emit exactly one project-scoped realtime "inbox item persisted" hint over the existing Web event path (`MohistHub` / `EventBridge`). The hint SHALL be emitted strictly after the inbox item is durably persisted; the server SHALL NOT emit the hint before persistence has succeeded. The hint SHALL carry only project identity and inbox-item identity — it is a delivery nudge — and SHALL NOT carry complete inbox state. The server SHALL NOT emit a hint when an insert is deduplicated and no new inbox item is created.

#### Scenario: Hint emitted after a new non-duplicate inbox item is persisted

- **WHEN** the server persists a new (non-duplicate) inbox item for a project
- **THEN** the server SHALL emit exactly one project-scoped realtime "inbox item persisted" hint
- **AND** the hint SHALL be emitted strictly after the item is durably persisted

#### Scenario: No hint before persistence succeeds

- **WHEN** an inbox item insert has not yet been durably persisted
- **THEN** the server SHALL NOT emit a realtime "inbox item persisted" hint
- **AND** a failed insert SHALL NOT produce a hint

#### Scenario: No hint on a deduplicated insert

- **WHEN** an inbox item insert is deduplicated and no new inbox item is created
- **THEN** the server SHALL NOT emit a realtime "inbox item persisted" hint

#### Scenario: Hint carries only project and inbox-item identity

- **WHEN** the server emits a realtime "inbox item persisted" hint
- **THEN** the hint payload SHALL identify the owning project and the inbox item
- **AND** the payload SHALL NOT carry complete inbox state

### Requirement: Realtime hint delivery is project-scoped with strict isolation

Realtime hint delivery SHALL be project-scoped. The server SHALL deliver an inbox hint only to Web sessions subscribed to the project that owns the inbox item. A session subscribed to project A SHALL NOT receive inbox hints for items owned by project B under any condition. Delivery SHALL reuse the existing per-connection subscription path and SHALL NOT introduce new multi-user routing.

#### Scenario: Hint delivered only to sessions subscribed to the owning project

- **WHEN** the server emits a project-scoped inbox hint for an item owned by project A
- **THEN** the server SHALL deliver the hint only to Web sessions subscribed to project A
- **AND** the hint SHALL be delivered over the existing per-connection subscription path

#### Scenario: No cross-project leakage to other project sessions

- **WHEN** project A and project B each have a realtime hint and sessions are subscribed to only one project each
- **THEN** a session subscribed to project A SHALL NOT receive any hint for project B items
- **AND** a session subscribed to project B SHALL NOT receive any hint for project A items

#### Scenario: No new multi-user routing is introduced

- **WHEN** realtime hint delivery occurs
- **THEN** delivery SHALL reuse the existing Web event path and per-connection subscription model
- **AND** SHALL NOT introduce new multi-user routing

### Requirement: Realtime hints are invalidation-only and the inbox HTTP API remains authoritative

The Web client SHALL treat every realtime hint as invalidation only. On receipt of a hint, the Web client SHALL re-query the inbox HTTP API to reconcile state and SHALL NOT interpret the realtime payload as complete inbox state, SHALL NOT locally synthesize inbox items from the hint, and SHALL NOT decide that an event becomes an inbox item. The durable `InboxItem` SHALL remain the product fact and the inbox HTTP API SHALL remain the source of truth. Browser reconnect, dropped hints, or otherwise missed realtime events SHALL NOT lose inbox data; the next inbox query SHALL reconcile truth.

#### Scenario: Client re-queries the inbox API on receipt of a hint

- **WHEN** the Web client receives a realtime "inbox item persisted" hint
- **THEN** the Web client SHALL re-query the inbox HTTP API to reconcile state
- **AND** SHALL NOT treat the hint payload as complete inbox state

#### Scenario: Client does not synthesize inbox items from a hint

- **WHEN** the Web client receives a realtime hint
- **THEN** the Web client SHALL NOT locally synthesize or persist an inbox item from the hint
- **AND** SHALL NOT decide that an event becomes an inbox item

#### Scenario: Reconnect or missed hint recovers via the next inbox query

- **WHEN** a browser reconnects, drops a hint, or otherwise misses realtime events
- **THEN** no inbox data SHALL be lost
- **AND** the next inbox HTTP API query SHALL reconcile truth
- **AND** the inbox HTTP API SHALL remain the source of truth over the realtime hint

### Requirement: App shell surfaces a live project inbox unread count

The Web app shell or project navigation SHALL display a project inbox unread count for the current project. The unread count SHALL update live as inbox items arrive or are marked read, without a manual refresh, by re-querying the unread count or inbox list on receipt of a realtime hint. The displayed count SHALL reflect only the current project's unread items and SHALL NOT include items from any other project.

#### Scenario: Unread count updates live when a new item arrives for the current project

- **WHEN** a realtime hint arrives for the current project while a Web session is open
- **THEN** the app shell unread count SHALL update without a manual refresh
- **AND** the updated count SHALL reflect the newly arrived item

#### Scenario: Unread count updates live when an item is marked read

- **WHEN** an inbox item in the current project is marked read
- **THEN** the app shell unread count SHALL update without a manual refresh
- **AND** the updated count SHALL reflect the read state change

#### Scenario: Unread count reflects only the current project

- **WHEN** the app shell displays the project inbox unread count
- **THEN** the count SHALL include only the current project's unread items
- **AND** SHALL NOT include unread items from any other project

### Requirement: Inbox page inserts or refreshes items live without a full page reload

When a realtime hint arrives for the project whose inbox page is currently open, the inbox page SHALL insert or refresh the affected inbox item without a full page reload, by re-querying the inbox HTTP API. The inbox page SHALL NOT perform a full browser navigation on receipt of a hint. Hints for other projects SHALL NOT cause the currently open inbox page to refresh.

#### Scenario: Inbox page refreshes the list on a hint for the current project

- **WHEN** a realtime hint arrives for the project whose inbox page is currently open
- **THEN** the inbox page SHALL insert or refresh the affected inbox item
- **AND** the refresh SHALL be sourced from the inbox HTTP API

#### Scenario: Inbox page does not perform a full page reload on a hint

- **WHEN** a realtime hint arrives for the current project
- **THEN** the inbox page SHALL NOT perform a full browser navigation or full page reload

#### Scenario: Inbox page ignores hints for other projects

- **WHEN** a realtime hint arrives for a project other than the one whose inbox page is currently open
- **THEN** the currently open inbox page SHALL NOT refresh or insert an item from that hint

### Requirement: High-attention inbox kinds show a lightweight in-app notice

For the high-attention inbox kinds (`workflow_failed` and `approval_requested`), the Web client SHALL show a lightweight in-app notice on receipt of a realtime hint, so that workflow failures and approval requests surface in-app while the operator is elsewhere. The notice SHALL be an in-app presentation only and SHALL NOT use browser push, mobile push, email, desktop notifications, sound, or any OS notification permission prompt. Notices for the non-high-attention kinds (`issue_started`, `issue_completed`) are not required by this capability.

#### Scenario: workflow_failed hint shows an in-app notice

- **WHEN** the Web client receives a realtime hint for a `workflow_failed` inbox item
- **THEN** the Web client SHALL show a lightweight in-app notice
- **AND** the notice SHALL surface the relevant issue context

#### Scenario: approval_requested hint shows an in-app notice

- **WHEN** the Web client receives a realtime hint for an `approval_requested` inbox item
- **THEN** the Web client SHALL show a lightweight in-app notice
- **AND** the notice SHALL surface the relevant issue context

#### Scenario: Notice uses no external push or OS notification

- **WHEN** the Web client shows a high-attention in-app notice
- **THEN** the notice SHALL be an in-app presentation only
- **AND** SHALL NOT use browser push, mobile push, email, desktop notifications, sound, or an OS notification permission prompt

### Requirement: Duplicate-notice suppression when already viewing the relevant context

The Web client SHALL suppress a high-attention in-app notice when the user is already viewing the relevant context for that inbox item — that is, the same issue the item refers to, or the same inbox item on the inbox page. A notice SHALL NOT fire when the user is already looking at the same issue or the same inbox item. Suppression SHALL be evaluated against the currently viewed context at the time the hint arrives.

#### Scenario: Notice suppressed when viewing the same issue

- **WHEN** a realtime hint arrives for a high-attention inbox item and the user is currently viewing the issue that item refers to
- **THEN** the Web client SHALL NOT show an in-app notice for that item

#### Scenario: Notice suppressed when viewing the same inbox item

- **WHEN** a realtime hint arrives for a high-attention inbox item and the user is currently viewing that same inbox item on the inbox page
- **THEN** the Web client SHALL NOT show an in-app notice for that item

#### Scenario: Notice fires when viewing an unrelated context

- **WHEN** a realtime hint arrives for a high-attention inbox item and the user is viewing an unrelated issue or an unrelated part of the app
- **THEN** the Web client SHALL show the in-app notice for that item
