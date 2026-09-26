# Transcript

The transcript presents the [Session input and Turn record](../input-and-turns/spec.md).
Its [presentation design](design.md) consumes facts without becoming an execution owner.

## Session Timeline

The timeline presents content in occurrence order. Routine progress must be
scannable, while required intervention must be visible immediately.

- Each entry states what happened, to which target, and with what result.
  Arguments, complete output, and diffs are collapsed by default.
- Mohist operations appear as domain actions with links to their targets.
- Failed execution and actions that need judgment remain prominent. Routine
  reads and searches are subdued. Consecutive routine entries may collapse into
  a summary, but failures and critical actions remain visible.
- Each input shows SessionInput acceptance and delivery. Several inputs may
  belong to one AgentTurn. Queued, executing, and terminal Turn phases appear.
- Silence is explicit. Queued work, waiting for a backend, idle, and unknown
  state never look like missing data.
- Compact and Reset create visible divider entries. Earlier content remains
  visible, and later work begins with empty context.

Summary shows the latest saved conversation and execution record. It updates
as Mohist saves progress, rather than displaying each arriving text fragment.
New saved progress and accepted or queued Input must appear without a manual
reload.

Summary omits recognized internal setup and recalled-memory blocks from input,
reply, and reasoning text. It keeps the surrounding conversation, including
every ordinary paragraph. An omitted block must not leave an empty message or
pretend that an attachment was sent. Unknown or unfinished blocks remain
visible. This is a readability rule, not a guarantee that sensitive content is
hidden.

Select **Raw** to inspect the original text and event payloads from the same
record, including the internal blocks omitted by Summary. Switching back to
Summary must not display text retained from Raw.

The page also supports model, usage, compaction records, current Activity,
Follow-up, Stop of a queued or active Turn, Compact, and Reset. Follow-up joins
the current execution while active or starts a new execution while idle. An
uncertain Stop remains Unknown. Compact uses the current backend's native
capability. Reset keeps the same AgentSession and makes later input use empty
Runtime context without showing physical Session history.

See [Action Contracts](../../workflow/actions/spec.md#shared-semantics-for-agent-execution-actions)
for Compact, Reset, and missing-Session recovery. See
[Session origins and identity](../input-and-turns/spec.md#agentsession-origin-and-addressing).
