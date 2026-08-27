## Scope review

- [ ] T-001 and T-002 implementation is limited to `RunnerGrain`, `RunnerState`, registry eligibility, and existing generation-fenced owner closeout seams.
- [ ] No Runner-process journal, generic queue/outbox, per-owner pending collection, public acknowledgment API, owner deadline API, interruption/Unknown API, or process-generation redesign is introduced.
- [ ] Presence timeout remains two minutes and the low-latency timer remains ten seconds.

## Correctness review

- [ ] Lease persistence precedes online registry publication.
- [ ] Activation restores only a future persisted lease; elapsed and missing leases fail closed.
- [ ] Unregister clears the lease before registry removal.
- [ ] Registry eligibility reads Runner authority and excludes expired or unavailable entries.
- [ ] ClosingProcessGeneration is set before closeout and survives reactivation.
- [ ] Both Workflow and AgentJob owner scans run even when an individual owner fails.
- [ ] Accepted and Refused retire an owner attempt; Outstanding, query failure, and exceptions retain the generation obligation.
- [ ] A replacement generation cannot reopen admission before the old generation closeout completes.
- [ ] Existing owner generation fences and idempotent behavior remain authoritative.

## Verification plan

Run focused Runner specs and checks specs with compiled apphost `-class`/`-method`, then `npm run test:fast`, `npm run docs:check`, `npm run archtest`, and `npm run verify`. Keep the worktree clean and ensure no `context.md` is created.
