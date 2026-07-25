### Requirement: Mention-triggered launch

A system handler SHALL subscribe to `com.mohist.issue.comment-added`. For each `@<token>` mention in
the comment body that resolves to an active Agent in the comment's project, the handler SHALL launch
that Agent once. The launch `prompt` SHALL be the full comment body verbatim (the `@` token is left
in place, not stripped), and the launch context SHALL be the comment's issue.

#### Scenario: Mentioning one Agent launches it

- **WHEN** a human adds a comment whose body contains `@<agent>` where `<agent>` is an active Agent's
  name in the project
- **THEN** that Agent is launched with the full comment body as its prompt and the issue as its context

#### Scenario: Prompt preserves the mention token

- **WHEN** a comment containing `@supervisor push this issue forward` triggers a launch
- **THEN** the Agent's prompt is the full comment body including the `@supervisor` token, unedited

#### Scenario: No mention means no launch

- **WHEN** a comment body contains no `@` mention of any active Agent
- **THEN** no Agent is launched for that comment

### Requirement: Token parsing

Mention detection SHALL parse `@` immediately followed by an Agent name, where the name is delimited
by whitespace and punctuation. Parsing SHALL be case-insensitive, and mentions within one comment
SHALL be de-duplicated by the resolved Agent. Only names SHALL be resolved — there is no id-based
lookup.

#### Scenario: Mention is delimited by whitespace or punctuation

- **WHEN** a comment body contains `@supervisor, please help.` or `@supervisor` at a word boundary
- **THEN** the token `supervisor` is extracted for resolution

#### Scenario: Mention matching is case-insensitive

- **WHEN** a comment body contains `@SuperVisor` and the project has an active Agent named `supervisor`
- **THEN** that Agent is resolved and launched

#### Scenario: Repeated mention of one Agent launches once

- **WHEN** a comment body mentions the same Agent name more than once
- **THEN** that Agent is launched at most once for that comment

#### Scenario: Distinct mentions each launch

- **WHEN** a comment body mentions several different active Agents
- **THEN** each distinct resolved Agent is launched independently for that comment

### Requirement: Loop prevention

A comment whose declared `author` matches the name of any active Agent in the project SHALL NOT be
scanned for mentions. Author-to-Agent name comparison SHALL be case-insensitive. Consequently an
Agent's own comment neither triggers other Agents nor re-triggers itself, and a mention chain can
only begin at a human-authored comment. Authorship is a declaration, not an authentication: a human
who signs a comment with an Agent's name produces a comment that does not trigger.

#### Scenario: Agent-authored comment does not trigger

- **WHEN** a comment's `author` equals (case-insensitively) the name of an active Agent in the project
- **THEN** the comment is not scanned for mentions and no Agent is launched from it

#### Scenario: Human-authored comment triggers normally

- **WHEN** a comment's `author` does not match any active Agent name in the project and the body
  `@`-mentions an active Agent
- **THEN** that Agent is launched

### Requirement: Resolution failure is a no-op

`@`-ing a name that does not resolve to an active Agent in the project SHALL NOT launch anything and
SHALL NOT fail the comment. The handler SHALL emit a structured log recording the unresolved name; no
system reply comment or inbox entry is produced for a typo. The only externally observable signal of
an unresolved mention is that nothing happens.

#### Scenario: Unknown name launches nothing

- **WHEN** a comment body contains `@nonexistent` and no active Agent by that name exists in the project
- **THEN** no Agent is launched and a structured log records the unresolved name

#### Scenario: Archived Agent is not resolved

- **WHEN** a comment body mentions a name that matches only an archived (non-active) Agent
- **THEN** that Agent is not launched (only active Agents resolve) and a structured log records the
  unresolved name

### Requirement: Per-comment launch idempotency

Launch idempotency for mentions SHALL be anchored on the comment identity — `(projectId, commentId,
agentId)` — not on the delivering event's id, because the comment (not the event) is the stable
anchor. Reprocessing the same comment (redelivery of its `comment-added` event, or repeated delivery)
MUST NOT launch any Agent more than once for that comment. The within-comment de-duplication required
above is part of this guarantee.

#### Scenario: Event redelivery does not relaunch

- **WHEN** the same comment's `comment-added` event is delivered more than once
- **THEN** each resolved Agent is launched at most once for that comment; no duplicate AgentJob is
  created

#### Scenario: Idempotency is scoped to the comment

- **WHEN** two different comments on the same issue both mention the same Agent
- **THEN** that Agent is launched once per comment (two launches total), because the idempotency key
  includes the distinct `commentId`

### Requirement: One-shot launch, no persistent subscription

A mention SHALL produce exactly one AgentJob. The system MUST NOT expand a mention into a watch, a
routing subscription, or any other persistent declaration. When the owner wants sustained attention,
the launched Agent fulfills that itself via `mo issue watch add`; the mention trigger carries no
ongoing-state semantics.

#### Scenario: Mention is a single job

- **WHEN** a comment mention launches an Agent
- **THEN** exactly one AgentJob is created for that (comment, Agent), with no watch or subscription
  created as a side effect

### Requirement: Launch-path reuse and provenance

A mention launch SHALL reuse the routed launch pipeline: issue context resolution, workspace
resolution, preflight validation, and preflight-failure handling (a preflight failure records a failed
AgentJob) SHALL behave identically to a routing-rule launch. The launch SHALL annotate its trigger
labels with the `commentId` and the `comment-added` event id, so the launch is traceable back to the
originating comment from both the AgentJob side and the comment side.

#### Scenario: Mention reuses workspace and preflight handling

- **WHEN** a mention launch is dispatched
- **THEN** workspace resolution, preflight validation, and preflight-failure recording behave the same
  as a routing-rule launch

#### Scenario: Mention provenance is recorded

- **WHEN** a mention launch creates an AgentJob
- **THEN** the AgentJob's trigger labels record the `commentId` and the `comment-added` event id,
  distinguishing a mention launch from a routing-rule or watch launch
