# AgentSession Timeline

The AgentSession timeline is a local presentation derived from transcript facts.
It is not a second Session record, event stream, or source of domain state. The
transcript contract and Session state authority remain in
[`agent-execution.md`](agent-execution.md). Product behavior is defined in the
AgentSession page section of [`../docs/web-ui.md`](../docs/web-ui.md).

The external Session event stream is a separate Server-owned projection. It has
its own sequence, cursor, retention, and strict field allowlist. It must not
reuse `TimelineItem`, browser subscriptions, or raw transcript facts as its
event record. This presentation model does not define that stream's ordering,
deduplication, or visibility.

## Design Drivers

- The client must derive a consistent, scannable view without persisting a
  second timeline model.
- Domain actions must be recognizable without hiding failures in shell output.
- Routine reads may collapse, but errors, inputs, and important actions must
  remain visible.
- Session Activity and AgentTurn state come from Server facts. The client must
  not infer them from heartbeats or item order.
- Context reset must be visible locally while the public stream exposes only a
  safe boundary event.

## Model

A `TimelineItem` is a presentation unit derived from a span of transcript
facts. Any client may implement the same derivation rules.

```text literal
TimelineItem
  Id            # Source identity: toolCallId, InputId, fact sequence, and so on
  RenderClass   # Presentation class
  Summary       # Verb + Object + Outcome?
  Salience      # Presentation salience
  GroupKey?     # Collapsible group key
  Detail?       # Expanded arguments, output, diff, or raw payload
```

Render classes are `input`, `message`, `reasoning`, `file-read`, `file-edit`,
`shell`, `domain-action`, `plan`, `tool`, `status`, `boundary`, `error`, and
`suppressed`.

Items have no terminal lifecycle. An in-progress item, such as an executing
tool call, is updated in place as facts arrive.

When the canonical `session.context_reset` fact occurs, the external stream
appends only a public `session.context_reset` boundary event. It contains
stable Session, Project, and Agent identities, public Session status, a safe
reason code, timestamp, and sequence. It contains no Runtime, path, prompt,
memory, or raw transcript data.

## Semantics

### Summary Projection

Summary consumes the saved public transcript and canonical Session
observations. The Server owns one complete-text projection before client
classification. It removes only complete, exactly recognized Mohist execution
envelopes and OpenViking context blocks. The recognized marker pairs have one
allowlist in that projection; clients must not copy its parser.

The projection applies to input, reply, and reasoning text regardless of role.
The client uses projected transcript text for those timeline classes; canonical
Input and Turn observations supply their separate state facts. The projection
preserves all text outside matched blocks, including multiple ordinary
paragraphs. Unknown markers and unmatched or unclosed blocks remain unchanged.
It must not guess internal content from ordinary prose or expand into general
diagnostic-payload redaction.

Text or reasoning parts emptied by this projection produce no Summary item.
Empty projected input text must not create a blank message or imply an
attachment. Real attachment evidence, Input acceptance, and Turn state remain
independent facts. Stored text and Raw responses remain unchanged. This
projection controls readability, not confidentiality; an unfinished block can
remain visible in a saved snapshot.

Summary replaces its text from accumulated public transcript parts. It must
not append Runtime text fragments or classify Raw detail payloads. No local
live tail may block a newer saved snapshot or survive a switch from Raw into
Summary. This boundary accepts saved-batch updates instead of per-token
rendering and requires no streaming parser or second accumulated transcript.

### Live Reconciliation and Identity

The open page follows the canonical AgentSession identity. A canonical Session
boundary or refresh hint invalidates its Session summary and transcript
queries even while idle, before a Runtime binding exists, or after that
binding changes. Compact, Reset, and Runtime replacement must not require the
page to reload. The refreshed Server snapshot determines the current binding
and state.

Canonical-only hints apply to the canonical page, not a historical Runtime
view, which remains scoped to its selected physical Runtime Session.

Runtime detail is separate from canonical invalidation. A detail must match
the page's canonical Session, physical Runtime Session, and Runtime before it
can enter the current live view. Missing or conflicting identity must not
relax that match. Envelope routing identities are authoritative; nested
payload fields must not replace them. Late detail from an old physical
Session cannot append current content or change current Activity.

The Server publishes a canonical-only `session.activity` refresh hint after
each saved transcript batch. It also publishes the hint after committing an
accepted or queued Input and its Turn state, including when no physical
Runtime Session exists. The hint carries canonical Session identity without a
physical Runtime identity. It is an invalidation signal, not a new Activity
transition, transcript fact, or domain event.

All writes needed by the refreshed snapshot must succeed before the hint is
published. A failed save or an empty persistence cycle publishes no hint. A
hint publication failure must not change committed execution state; existing
reconnect reconciliation restores the saved snapshot. No separate delivery
queue is added for these hints.

The client uses the existing Web event channel and query refetch mechanism to
replace Summary from saved state. Raw Runtime details may update the Raw view;
they do not trigger one Summary HTTP request per text fragment. Saved-batch
hints, canonical boundaries, and reconnect reconciliation drive Summary
refresh. This path adds no timer, polling loop, parser, or configuration flag.

### Classification

Classification is a pure function from transcript fact sequence to item
sequence. Classification and rendering are separate. Presentation components
consume only `TimelineItem` values.

```text diagram
               +-------+
               | Facts |
               +---+---+
                   |
                   v
         +-------------------+
         | Try domain action |
         +---------+---------+
         +---------+---------+
         vrecognized         vnot recognized
 +---------------+   +---------------+
 | domain-action |   | Try tool type |
 +---------------+   +-------+-------+
                     +-------+-------+
                     vrecognized     vnot recognized
                 +------+   +----------------+
                 | tool |   | shell fallback |
                 +------+   +----------------+
```

Classification tries these paths in order:

1. Recognize a Mohist domain action.
2. Map a known tool type.
3. Use the source class as fallback, such as `shell`.

Every fact belongs to exactly one class. A failed outcome changes the item's
RenderClass to `error`, retains its original Summary, and appends the failure
fact. Failed recognition must use the fallback and must not invent semantics.

### Domain Operation Recognition

Two sources produce the same `domain-action` item:

- The Shell path parses a bash-like `mo` command, extracts its command group
  and verb, and maps arguments such as Issue number and WorkflowRun ID to the
  Object and page link.
- The Tool path maps a Runtime or MCP tool name through the Mohist domain
  operation list.

The command exit result determines Outcome. A failed result is `error`. Both
paths produce the same RenderClass and sentence form; only a source marker may
differ. A command that is not a known `mo` operation, or whose group cannot be
parsed, remains a `shell` item.

### Sentence Form and References

- Build `Summary` as `Verb + Object + Outcome?`. Put Outcome where the reader
  sees success or failure immediately.
- Resolve Object to a recognizable name or link. Issue numbers link to Issues,
  Agent names identify Agents, and run IDs link to runs. Do not show bare
  internal IDs.
- If a complete sentence is not possible, state only the fact, such as
  `Ran X`. Do not add imagined content.

### In-Place Updates

- `tool_call.started`, `updated`, and `completed` update one item by
  `toolCallId`.
- `completed` and `failed` are irreversible terminal states. Late facts cannot
  move them backward.
- Text and reasoning keep their message association when a saved snapshot
  replaces Summary. Item order follows the saved fact sequence.
- A fallback item may be promoted when facts become complete, for example from
  `shell` to `domain-action`. Its Id does not change.

### Collapsible Groups

Collapse a consecutive sequence of at least three low-salience items of one
class: `file-read`, successful `shell`, or `tool`. Keep the group expandable
and prefer items with the same `GroupKey`.

`error`, `domain-action`, `input`, `message`, `status`, `boundary`, and
`suppressed` never enter a group and break a consecutive sequence. Failures and
important actions therefore remain visible.

### Salience and Status

Salience order is:

1. `error`
2. write `domain-action`
3. `input` and `message`
4. `file-edit` and `shell`
5. `file-read`, `tool`, and `reasoning`
6. `status` and `suppressed`

Salience affects prominence, grouping eligibility, and the current activity
summary only. It is never written to domain state and never participates in
Session state derivation.

The UI presents these Server facts without inference. Activity, AgentTurn
state, and transcript status facts come from the Server. The client does not
infer state from heartbeats or item order.

- A queued Turn shows the input and `Queued`.
- An executing Turn with no new item shows `Executing` and the latest readable
  nonterminal item, skipping `status` and `suppressed`. With no active item,
  it shows the Turn state.
- `idle` and `unknown` have distinct presentations. `unknown` never renders
  as idle.

### Raw View

A page-level toggle shows the same timeline data in raw fact order: one row per
transcript fact with an expandable payload. The two views are two levels of the
same data, not two feeds. Raw retains original text and payloads and may append
matching Runtime detail from the existing channel. Switching views anchors the
scroll position by item Id without carrying Raw text into Summary.

The raw view is a controlled Web diagnostic presentation. It is not an external
API export. Direct callers receive only the public projection from
`agent-api.md`, which excludes prompts, memory, paths, Runtime or Connection
identity, and raw payloads.

## Examples

1. `tool_call.completed{bash, "mo issue comment create 42 --body ...", exit 0}`
   becomes the `domain-action` item `Commented on #42`, linked to Issue #42.
2. The same command with a nonzero exit becomes `error`: `Failed to comment on
   #42`. It remains prominent and is never grouped.
3. Three reads and two greps collapse to `Read 5 files`. If the third operation
   fails, the first two collapse, the failure stays visible, and the final two
   form a new group.
4. `session.context_reset{reason: "reset"}` becomes the `boundary` item
   `Context reset`. Later items belong to the new Runtime context.

## Status

The Web has a classified timeline and a Raw view. Summary still combines saved
transcript text with locally appended Runtime details. Public filtering covers
only some internal input blocks and can discard ordinary text. Real-time
delivery can precede transcript persistence, and Runtime-bound matching can
leave the canonical page stale after replacement. Saved-batch refresh hints
and the single complete-text projection remain implementation gaps.
