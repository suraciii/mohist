# Self Review: Issue 477

Reviewed the current Issue 477, proposal, design, task breakdown, capability specs, and governing design constraints.

## Findings

1. **[BLOCKER] Live resolution does not define the Run's stage topology when a bound Profile changes.** The issue requires later stages of an active Run to use the bound Profile's latest Definition. However, the design creates the Run's Stage lifecycle from the Definition at startup (`design.md:102-105`) and later resolves only an already-known uninitialized Stage by name (`design.md:103-109`). It defines an error when a future pre-existing Stage is removed, but it does not say whether a newly added Stage is scheduled, where it is placed, or how a reordered Definition is interpreted. Consequently, two conforming implementations can produce different stage sequences after the same Profile edit, and neither `workflow-profile-live-resolution` nor T-003 tests additions or reordering. Define one deterministic rule for additions, removals, and reordering (or explicitly reject topology-changing edits while active Runs are bound), then add scenarios and server specs for it.

<promise>FAIL</promise>
