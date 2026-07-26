## Review

The proposal, specification, design, and task graph consistently require the indexed activity projection and EF migration needed to bound direct-session selection. The status candidate contract preserves the existing response, ordering, project boundary, Workflow pending-work check, and request-work amplification semantics.

The design and task plan cover database-side candidate selection, one materialization per selected Session, one Workflow status read per distinct run, and deterministic operation-count tests with irrelevant history and cross-project Workflow references. The single task is a complete vertical slice with no unresolved dependencies.

<promise>PASS</promise>
