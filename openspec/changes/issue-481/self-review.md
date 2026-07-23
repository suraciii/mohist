# Self-Review - Issue 481

Reviewed `proposal.md`, `design.md`, `tasks.json`, and all capability specs against the issue and the current Activity/Event implementation.

## Prior Findings - Verified Fixed

- **P1 (Activity source):** `activity list` is no longer a projection of live AgentSession cards alone. The plan uses the persisted `ProjectEventFeedAssembler` collection for Issue, WorkflowRun, and AgentSession history, then combines the existing AgentOps/waiting and Runner snapshots without adding storage or changing source contracts. `ActivityEntryDto` defines stable identity, provenance, kind, time, source identities, and the final bounded ordering. The proposal, activity spec, design D1/D2, and T-001/T-002 now agree.
- **P2 (Runner scope):** Runner visibility is now explicit rather than falsely Project-isolated. The plan records `scope=project` for Project Event, AgentOps, and waiting evidence, and `scope=global` for Runner snapshots. This matches `RunnerRegistryGrain.ListEligibleRunnersAsync`, which intentionally returns the global registry regardless of `projectId` (`RunnerRegistryGrain.cs:137-140`). Cross-Project tests now assert isolation only for Project-bound entries and permit shared Runner context only when it is marked global.

## Verified Correct

- **Activity semantics:** `provenance` distinguishes recorded history from snapshots, while `scope` distinguishes the resolved Project's evidence from global Runner context. Bare `--json` exposes both fields, so scripts do not need to infer either property from titles or raw event types.
- **Event tail:** The plan keeps the existing project-scoped server match compiler, post-subscription-only NDJSON behavior, selected-field stream projection, and cancellation exit `130`.
- **Dead-letter recovery:** The singular CLI migration retains existing list/redeliver behavior, loopback-only credential preflight, explicit operator credential lookup, server error diagnostics, and terminal-control sanitization.
- **Command separation:** The plan rejects plural `events` and `event list`, keeps routing independent, and gives each entry separate help without a mode/source multiplexer.
- **Plan integrity:** `tasks.json` is valid JSON with an acyclic, strictly-lower-priority chain (`T-001 -> T-002 -> T-003`). Every capability has a spec, every requirement has at least one `#### Scenario`, and task acceptance criteria include the relevant server or CLI test command.

## Verdict

The plan is internally consistent with the issue and current source boundaries, preserves the stated non-goals, and is ready to build.

<promise>PASS</promise>
