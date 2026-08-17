# Design

`WorkflowRunWorkProjectionBuilder` already projects `ActiveWorkId` as null
when an Agent settlement is unresolved, and the workflow commit writes
`AttentionStatus=blocked` for the deadline transition. The Runner's SQL
querier is the remaining accounting boundary: it currently filters only on
`Status=Running` and `AssignedWorkerId`.

Add a nullable-safe `AttentionStatus != "blocked"` predicate to both
`CountRunningAssignedToAsync` and `FindRunningAssignedToAsync`. This keeps the
change at the indexed control-plane read boundary, does not deserialize the
workflow state, and leaves the original assignment untouched for receipt
identity checks. A blocked row therefore disappears from `activeWorks`, slot
accounting, and redelivery on the next poll, while a matching late report can
still use the existing settlement path.
