# Self Review

## Findings

### P1: Fallback permits an event with a missing physical binding to bypass a known binding

`design.md` says logical fallback is permitted when the physical runtime ID is missing "on either side" (lines 43-46), while the live-content spec requires an available current physical identity to participate in matching and requires known physical mismatches to be rejected. An event from an earlier runtime can retain the same logical session ID but omit its physical ID; the planned matcher would append it to a current runtime view solely because the page knows the current ID. Restrict logical fallback to the case where the page itself lacks a physical-runtime anchor, and reject an event whose physical identity is absent when the page has one. Update the task acceptance matrix to cover this stale-event case.

### P1: Workflow-origin envelope expansion violates the existing realtime boundary without an actual missing-ID producer

The design and `T-001` add `projectId`, `issueNumber`, `workflowRunId`, and `sessionName` to `TranscriptEnvelope` to support a missing-logical-ID workflow fallback (`design.md` lines 53-59; `tasks.json` lines 9-16). The current transcript envelope is explicitly session-scoped, its `SessionId` is required, and the server fan-out constructs it from the canonical session ID. No artifact identifies a real producer that emits a workflow transcript event without that ID. The plan therefore expands the best-effort wire contract against its documented boundary, exposing workflow metadata to all transcript subscribers, for a scenario that the current pipeline cannot produce. Revise the specs/design/tasks to either use the existing canonical logical ID plus the safe missing-page-binding fallback, or identify the concrete legacy producer and justify a narrowly scoped compatibility path that does not change the canonical envelope contract.

<promise>FAIL</promise>
