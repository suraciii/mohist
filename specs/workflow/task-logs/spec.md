# Task Log

TaskLog preserves bounded execution evidence for a Task. It explains how work
reached a WorkResult without making high-volume process output part of Workflow
state.

## Read Contract

Each entry exposes only monotonic `seq`, timestamp, source, and redacted text.
Reads use owner and work identity, with sequence as cursor and jump anchor.
Storage layout and index names are not part of this contract.

The related records have separate meanings:

- Transcript records what the Agent said and did. It belongs to the Session.
- Artifact records which files were produced. It belongs to the Workflow.
- WorkResult records whether work succeeded and its structured result. It
  belongs to the AgentJob.
- TaskLog records execution evidence. It belongs to Runner execution.

## Status

The current store derives terminal ownership heuristically from WorkflowRun
state. The target design requires settlement to persist ownership before
accepting the terminal flush.
