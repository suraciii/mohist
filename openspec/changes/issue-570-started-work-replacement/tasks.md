# Tasks: Explicit Started-Work Replacement

- [ ] Add immutable supersession disposition and `Superseded` TaskRun state,
      event, read projection, and migration coverage.
- [ ] Implement the serialized grain transition with exact tuple fencing,
      request-id idempotency, assignment handoff, reminder/snapshot cleanup,
      and late-receipt staleness.
- [ ] Add the run-scoped authenticated API control and blocked-only guard.
- [ ] Add the CLI command and its transport/selected-JSON contracts.
- [ ] Run focused domain, grain, API, and CLI tests before any live operation.
