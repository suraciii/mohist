# AgentSession Timeline

This document defines the presentation model for the AgentSession page timeline: it derives
scannable activity items from transcript facts. It defines presentation derivation only, not a
second session record. The transcript contract and Session state authority remain unchanged
(see [`agent-execution.md`](agent-execution.md)). See the AgentSession page section in
[`../docs/web-ui.md`](../docs/web-ui.md) for product behavior.

## Model

**Timeline item (`TimelineItem`)**: a presentation unit derived from a span of transcript facts.
It is derived locally by the client: it is not persisted, published to the event bus, or written
back to the Server. Any client can implement the same rules independently.

```text literal
TimelineItem
  Id            # Determined by the source fact: toolCallId, InputId, fact sequence, and so on
  RenderClass   # Presentation class
  Summary       # Form: Verb + Object + Outcome?
  Salience      # Salience
  GroupKey?     # Collapsible group key
  Detail?       # Expanded content: arguments, full output, diff, raw payload
```

Presentation classes:

| RenderClass | Source fact | Example reading |
|---|---|---|
| `input` | SessionInput | Input content plus acceptance/delivery state |
| `message` | text | Agent response |
| `reasoning` | reasoning | Reasoning, collapsed by default |
| `file-read` | tool (read / grep / glob / list, and so on) | `Read x.ts` |
| `file-edit` | tool (edit / write, and so on) | `Edited x.ts (+12/-3)` |
| `shell` | tool (bash, and so on) | `Ran npm test -> passed` |
| `domain-action` | Recognized Mohist domain operation | `Commented on #42`; `Approved the Plan stage for #42` |
| `plan` | todo / plan tool | Plan and completion progress |
| `tool` | Any other tool | Honest fallback: `Ran X` |
| `status` | session.activity, model, usage, provider.retry, and so on | Muted status row |
| `boundary` | compaction, session.context_reset | `Context reset` boundary |
| `error` | turn.failed or any failed item | Prominent failure card |
| `suppressed` | Deliberately de-emphasized noise fact | Single muted line |

Items have no terminal lifecycle. In-progress items, such as an executing tool call, are updated
in place as facts arrive.

## Semantics

### Derivation and classification

- Classification is a pure function: transcript fact sequence -> item sequence. Classification
  is separate from rendering; presentation components consume only `TimelineItem` values.
- Classification tries, in order, `domain-action` recognition -> tool type table -> `tool`
  fallback. Failed recognition must degrade to the fallback and must not invent semantics.
- Each fact belongs to exactly one class. Failure rewrites the result: when any item has a failed
  outcome, its RenderClass is `error`; its original Summary is retained and the failure fact is
  appended.

### Domain operation recognition

Two paths converge on the same `domain-action` item:

1. **Shell path**: parse `mo` commands run by bash-like tools. Extract the command group and verb
   (`issue comment create`, `run approve`, `issue start`, and so on), map them to Verb, and parse
   arguments such as Issue number and WorkflowRun id into Object and page links.
2. **Tool path**: map directly when a Runtime or MCP tool name matches the Mohist domain operation
   table.

The command exit result determines Outcome; a failure produces `error`. Both paths produce the
same RenderClass and sentence form. Only the source marker may differ. A command that is not a
known `mo` operation, or whose command group cannot be parsed, remains a normal `shell` item and
is never promoted speculatively.

### Sentence form and reference resolution

- Construct Summary as `Verb + Object + Outcome?`; make Outcome immediately visible so success
  or failure can be identified at a glance.
- Resolve Object to a recognizable name or link: an Issue number links to the Issue page, an
  Agent uses its name, and a run id links to the run. Do not show bare internal ids.
- When a complete sentence cannot be constructed, retain an honest statement of the fact, such
  as `Ran X`, without adding imagined content.

### In-place updates

- `tool_call.started / updated / completed` update one item by toolCallId. Terminal states
  (`completed / failed`) are irreversible; late facts cannot move them backward.
- Append streaming text / reasoning by message association. Seal the current stream before
  inserting a non-text item; a later chunk starts a new item.
- An item may first appear in a fallback class and be promoted when facts become complete, for
  example from `shell` to `domain-action`; its Id does not change.

### Collapsible groups

- Collapse a consecutive sequence of at least three low-salience items of one class
  (`file-read`, successful `shell`, or `tool`) into a summary such as `Read 5 files`. The group
  remains expandable, and items with the same GroupKey are grouped preferentially.
- `error`, `domain-action`, `input`, `message`, `status`, `boundary`, and `suppressed` never enter
  a group and break a consecutive sequence. Failures and important actions therefore always
  remain visible outside collapsed groups.

### Salience

From highest to lowest: `error` -> write `domain-action` -> `input` / `message` -> `file-edit` /
`shell` -> `file-read` / `tool` / `reasoning` -> `status` / `suppressed`.

Salience affects presentation only: prominence, grouping eligibility, and selection of the
current activity summary. It is never written back to domain state and does not participate in
Session state derivation.

### Silence and status presentation

- Turn `queued` -> the input item and status row show `Queued`.
- Turn `executing` with no new item -> the current activity bar shows `Executing` and uses the
  latest readable nonterminal item, skipping `status` / `suppressed`. When no item is in progress,
  it shows the Turn state itself.
- `idle` / `unknown` -> distinct idle / unknown presentations. `unknown` must not render as idle.
- All of these states come from Server facts: activity, AgentTurn state, and transcript status
  facts. The client does not infer state from heartbeats or from the item sequence, consistent
  with the consumer rules in [`agent-execution.md`](agent-execution.md).

### Raw view

- A page-level toggle switches the same timeline data to raw fact order: one row per transcript
  fact with an expandable payload.
- The two views are two levels of the same data, not two feeds. Switching anchors the scroll
  position by item Id.

## Examples

1. `tool_call.completed{bash, "mo issue comment create 42 --body ...", exit 0}` becomes the
   `domain-action` item `Commented on #42`, which links to Issue #42.
2. The same command with a nonzero exit becomes `error`: `Failed to comment on #42`. It is
   prominent and never grouped.
3. A consecutive read x3 plus grep x2 collapses to `Read 5 files`. If the third operation fails,
   the first two collapse, the failure remains prominent, and the final two form a new group.
4. `session.context_reset{reason: "reset"}` becomes the `boundary` item `Context reset`; later
   items belong to the new Runtime context.

## Status

The current Web implementation is a conversational message view: turns are grouped and tools
use classified cards. It has no `TimelineItem` derivation layer or salience policy. Context-tool
groups do not break on failure, `mo` domain operations are not recognized, and SessionInput
acceptance and AgentTurn state appear in a separate evidence area rather than in the timeline.
There is no raw event view.

Transcript facts, persistence, and real-time delivery are already implemented. This model needs
no new transcript facts and does not change Server responsibilities; all derivation can happen
locally in the Web client. An implementation issue still needs to be created from this spec.
