### Requirement: Comment-added event emission

Adding a comment to an issue SHALL emit a CloudEvent of type `com.mohist.issue.comment-added`.
Today the comment-add path persists the comment row and returns; it MUST additionally emit this
event. The event `data` payload SHALL carry `commentId`, `author`, and `body`. The `commentId`
SHALL be the stable identity of the persisted comment row; `author` SHALL be the normalized declared
author; `body` SHALL be the comment body verbatim.

#### Scenario: Adding a comment emits the event

- **WHEN** a comment is added to an issue (via the API or `mo issue comment add`)
- **THEN** a `com.mohist.issue.comment-added` event is emitted whose `data` carries the comment's
  `commentId`, `author`, and `body`

#### Scenario: Comment is persisted before the event is observable

- **WHEN** a comment-add completes successfully
- **THEN** the comment row is durable before any subscriber can observe the `comment-added` event, so
  a handler reading the comment by `commentId` always finds it

### Requirement: Issue lineage stamping

The `comment-added` event SHALL be stamped with the same issue lineage as other `issue.*` events: the
`projectid` and `issue` (issue number) extensions MUST always be present, and the `epic` extension
SHALL be present when the issue belongs to an epic and omitted otherwise. Lineage SHALL be derived
purely from the comment's issue, with no cross-aggregate lookup.

#### Scenario: Event carries project and issue

- **WHEN** a comment is added to an issue
- **THEN** the emitted `comment-added` event's extensions include `projectid` and `issue` equal to the
  comment's project and issue number

#### Scenario: Epic is stamped when present

- **WHEN** a comment is added to an issue that belongs to an epic
- **THEN** the emitted `comment-added` event's extensions include `epic` equal to that epic number

#### Scenario: Epic is omitted when absent

- **WHEN** a comment is added to an issue with no epic
- **THEN** the emitted `comment-added` event's extensions omit `epic` entirely (never an empty value)

### Requirement: Comments are the only trigger source

Only adding an issue comment SHALL emit `comment-added`. Creating or editing an issue — including
changes to the issue body or title — MUST NOT emit `comment-added`. An `@` in the issue body is a
reference, not a trigger, and MUST NOT produce this event.

#### Scenario: Issue body edit does not emit comment-added

- **WHEN** an issue's body or title is created or edited and the body contains an `@` token
- **THEN** no `comment-added` event is emitted

#### Scenario: Comment add is the sole source

- **WHEN** any non-comment issue mutation occurs
- **THEN** no `comment-added` event is emitted
