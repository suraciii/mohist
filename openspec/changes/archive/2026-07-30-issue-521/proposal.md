## Why

After an Agent finishes a turn, users naturally want to keep talking in the same context — add a condition, ask for a change, ask why it did something. The design spec already defines AgentSession as a durable logical session with stable `SessionInput` and `AgentTurn` subrecords, three-valued follow-up results, and idempotent retry, but only the **launch** path landed those subrecords. Follow-up inputs today land only as flat `session.input` transcript events with no stable identity, no distinct turn lifecycle, a binary `sent`/error sync result, and no client idempotency — so a lost response means a duplicate input, and users cannot tell "accepted, pending" from "executing". This change closes the gap so a follow-up is a real continuation of the same session, not a transcript log line.

## What Changes

- A follow-up accepted by Mohist now persists a stable `SessionInput` subrecord (stable Id, sequence, acceptance) synchronously at acceptance time, instead of relying solely on a flat transcript event written later by the runner.
- Each follow-up execution round is modeled as a stable `AgentTurn` subrecord that consumes one or more queued inputs in order and progresses `queued → executing → terminal`, mirroring how the launch turn is already tracked.
- Inputs submitted while a turn is executing are accepted and queued — joined to the current turn when the runtime supports it, otherwise assigned to a subsequent turn — without interrupting the running turn, and without being dropped, overwritten, or merged into another input.
- Input acceptance and Turn status together let the user distinguish "input accepted, pending" from "input being executed".
- The follow-up sync result becomes the three-valued `accepted` / `rejected` / `unknown` contract, returning the stable Input (and Turn) identity to the caller.
- A client-provided idempotency key makes a follow-up retried after a lost response return the original `SessionInput` instead of creating a second input.
- Web and CLI submit follow-ups with the same idempotency-key transport and render the same accepted/pending-vs-executing status model.
- follow-up continues to **not** create a new `AgentJob`; the launch AgentJob keeps owning only the first execution, and the AgentSession stays usable after it terminates.

## Capabilities

- `agent-session-followup-input`: A follow-up persists a stable `SessionInput` subrecord with a stable Id and acceptance state at acceptance time; once accepted it survives restart and is never silently dropped, overwritten, or merged, and a retried follow-up with the same call identity returns that same Input instead of a second one.
- `agent-session-followup-turn`: A follow-up execution round is a stable `AgentTurn` subrecord consuming one or more inputs in order and progressing `queued → executing → terminal`; inputs submitted during an executing turn are accepted and queued for the current or a subsequent turn without interrupting, dropping, or merging, and Turn status versus Input acceptance lets users distinguish "accepted, pending" from "being executed".
- `agent-session-followup-call`: The follow-up call returns the three-valued `accepted` / `rejected` / `unknown` sync result with stable Input and Turn identity, and Web and CLI use the same idempotency-key transport and the same status interpretation.

## Impact

- **Server Session domain & grain:** the follow-up path (`AgentSessionGrain.BeginFollowupAsync` / `ConfirmFollowupAsync` and its lease lifecycle) must mint and persist `AgentSessionInputRecord` / `AgentTurnRecord` subrecords for follow-up inputs (the record types already exist; today only the launch path populates them), look up inputs by client idempotency key, and progress follow-up turn status on runtime events.
- **Server API:** `AgentSessionFollowupRoutes` and the issue-scoped follow-up alias in `IssueRoutes.Sessions` change the sync result to the three-valued model, accept a client idempotency key, and return stable Input/Turn identity.
- **Runner:** `followup-handler` must report Input/Turn acceptance and terminal facts back to the Server grain (today it only writes flat transcript events), and the operation-journal dedup scope aligns with the client call identity.
- **Web:** `useFollowupMutation`, `SessionFollowupComposer`, and the `coder-session` API client add the idempotency key and render accepted/pending versus executing status.
- **CLI:** `mo session followup` gains the idempotency-key flag and surfaces Input/Turn identity and the new status values.
- **AgentJob:** unchanged — it keeps owning only the launch execution; no new AgentJob is created by follow-up.
- **Persistence & dependencies:** no new external dependency; the subrecord types and storage already exist, so the change is populating and progressing them on the follow-up path plus aligning the result model.
