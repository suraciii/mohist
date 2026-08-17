# Design

`WorkflowRunWorkProjectionBuilder` is the durable accounting boundary. It
already has the full workflow state and can distinguish `Unknown` from
`Blocked`; therefore it keeps `ActiveWorkId`/`ActiveWorkerId` for Unknown and
clears them only for Blocked.

`WorkflowRunQuerier.CountRunningAssignedToAsync` and
`FindRunningAssignedToAsync` are the Runner control-plane boundary. Their
database predicates now require `Running`, the assigned runner, a non-null
active work id, and the same active worker id. They continue to avoid
deserializing workflow state. This releases blocked rows from slot accounting
and redelivery without changing the original identity fence.
