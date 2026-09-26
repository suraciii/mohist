# Interaction

A configured [Connection](../connections/spec.md) accepts Slack input.
[Delivery](../delivery/spec.md) owns the corresponding visible reply and
Session card; [enrollment](../enrollment/spec.md) owns installation.

## Slack Message Permissions

Mohist processes Bot DMs, explicit mentions, and replies in bound threads.
Other channel messages are discarded before a durable record or log.

The configuration view lists every requested permission and its reason. Invite
the Bot only where it is needed. The first version has no per-permission
toggles and no per-Connection channel list. The Bot accepts invocations from
every channel where it is present.

Mohist reads only basic member identity: IDs, names, and avatars. It never reads
member email, gives the directory to an Agent, or keeps it after Connection
delete.

Socket Mode needs no public inbound address. Slack retains unconfirmed
messages only briefly. Delayed Events may help during an outage, but recovery
is not indefinite. After a long outage, the status view warns that messages
may be missing and asks the user to resend critical delegations.

## Use Slack

### Start New Work

New work starts from a DM with no current Session or from a channel root message
that mentions the Bot.

After removing the mention, the message must contain task text or a usable
attachment. A bare mention gets a question, not an AgentJob. An attachment
alone is valid input.

On acceptance Mohist creates the AgentJob, AgentSession, first SessionInput, and
first AgentTurn. The **👀 Received** reaction marks acceptance. Liveness then
shows whether work is running or queued. A queued or running Turn can be
stopped. Agent replies, failures, and requests for human action return to the
same conversation.

A signed action button performs a supported operation, such as Stop or Retry,
under the presser's authority. Buttons are shortcuts to CLI and Web operations,
not a second command grammar.

Completing an AgentJob does not close its AgentSession. A user can answer a
question in the same conversation.

### Continue the Same Session

In a channel, a reply in the bound thread follows up the bound Session. In a
DM, every ordinary message continues the current Session, even after a Turn
ends. To start a fresh Session, begin the DM with `new task` followed by task
text. Separate channel threads provide parallel Sessions.

Follow-ups do not create another AgentJob. Every accepted message becomes a
SessionInput with stable identity. Input during execution steers the current
Turn or waits for the next one. Only explicit Stop interrupts. A full Session
queue rejects new messages and asks the sender to retry later. Accepted input
is never discarded.

Runtime failure does not end the Slack conversation. A DM input waiting for its
first Runtime binding is queued. A retry-safe infrastructure failure retries the
recorded work with its original snapshot, moves the DM route to the replacement
Session, and then accepts the current message there. The current input's message
and thread become the replacement execution's reply anchor; the failed input
remains retry history and never redirects a new reply into an unrelated old
thread. Slack redelivery resolves to the same retry, reply anchor, and
SessionInput.

An idle Session whose physical Runtime Session is confirmed missing is recovered
on the same Runner and logical AgentSession. Mohist never automatically replays
input while execution is active or its effects are unknown. Those states need
explicit reconciliation. `new task` is an intentional command, never recovery.

One thread can host several Agents:

- One bound Agent: an unmentioned reply continues its Session.
- Several bound Agents: an unmentioned reply is discussion; mention the target
  Bot.
- Mentioning another Bot starts an independent Session without contaminating
  the original one.
- One message mentioning several Bots starts no work and shows one chooser.
  Choosing an Agent starts exactly one execution from the original message
  under the selected Connection and Project. The signed chooser expires after
  five minutes and survives a Server restart without rerunning the prompt.
- A Bot's own message never becomes input for itself or another Bot.

Separate Mohist Servers do not coordinate one multi-Bot message.

### Mention in an Existing Discussion

A first Bot mention in a human discussion passes the Bot-visible thread history
as initial context and treats the mention as the task. Oversized context is
truncated oldest-first and marked in the Agent input and Slack confirmation.
If permissions, rate limits, or a Slack failure prevent a complete read,
Mohist rejects the delegation and creates no AgentJob.

Imported history is untrusted user input, not Instructions. Its maximum impact
is bounded by the Agent's configured capability. Editing an accepted message
does not rerun it. Deleting a message does not remove its AgentJob, Session, or
audit record.

### Files and Links

The Bot reads files that the message or thread explicitly provides. The files
become input attachments with their source preserved. An unreadable, oversized,
or unsupported file is reported as unused. Links remain message text. Mohist
does not fetch URLs; an Agent opens one only through its configured Skills and
Runtime permissions.

## Slack Collaboration Rules for Agents

Mohist injects these rules as a visible, evolving Skill:

- Reply with the send action to the injected anchor. Reasoning and tool calls
  are invisible. Send a useful conclusion; send nothing when there is no new
  information.
- Do not send an empty acknowledgement. Silence is normal completion. Answer a
  direct human question even when there is nothing new to add.
- Call back after delegated work completes. Mention the delegator when the
  result needs their attention.
- Keep replies self-contained and proportionate. Put fine-grained progress in
  the Web Session timeline.
- Never guess the reply location. Mohist supplies the thread and message
  anchor.
- Resume silently after restart, Session recovery, or context compaction. Do
  not announce the interruption or ask how to proceed.
