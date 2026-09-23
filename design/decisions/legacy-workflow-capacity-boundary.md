# Workflow Sessions and Agent Capacity

Agent capacity must not be blocked by workflow-session rows that are not owned by an Agent.

## Design Drivers

Capacity is derived from Agent jobs and Agent-owned sessions. Workflow sessions use a separate ownership boundary and may use a different JSON shape. Treating every unowned workflow session as Agent ownership evidence makes unrelated workflow history appear to be an unresolved Agent owner.

## Semantics

A session is considered for Agent capacity evidence only when it is an Agent-owned session or when its metadata does not identify it as a workflow session. A row whose metadata contains `mohist.io/source-kind = workflow`, regardless of JSON property casing, is not Agent ownership evidence and is ignored by the Agent capacity projection when it has no Agent owner label.

Malformed rows that are not positively identified as workflow sessions remain incomplete evidence and continue to fail closed. A workflow session that explicitly carries an Agent owner label remains eligible for the normal Agent ownership checks because it is loaded through the Agent-owned session path.

## Example

A workflow row with `Metadata.Labels["mohist.io/source-kind"] = "workflow"` and no Agent owner must not make an otherwise healthy Agent report `dispatch-pending`. A malformed row with `mohist.io/source-kind = "agent-launch"` must continue to produce incomplete owner evidence.

## Alternatives considered

- Deleting or rewriting historical workflow rows would alter unrelated durable history and is unnecessary for capacity projection.
- Treating every non-deserializable row as missing Agent ownership would preserve fail-closed behavior but incorrectly block Agents on unrelated workflow history.

## Status

Status: accepted

Implemented in the Agent capacity projection and covered by storage-level tests.
