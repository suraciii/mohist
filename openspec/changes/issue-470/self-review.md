# Self-Review - Issue 470

Reviewed `proposal.md`, all three capability specs, `design.md`, and
`tasks.json` against issue #470, the live Server/CLI implementation, the
observability design baseline, and the repository testing constraints.

## Verdict

The plan is not ready to build. The capability coverage is broad and the task
graph is structurally valid, but several contracts either contradict the issue
or cannot produce trustworthy values under the planned default configuration.
The technical design also leaves two bounded-cost guarantees without a viable
implementation/test boundary.

## Findings

### F1 - Agent endpoint contract contradicts the issue (high)

Issue #470 names `/api/agent/status` and `/api/agent/activity` in its acceptance
criteria. The plan instead specifies only
`/api/projects/{projectRef}/agent/status` and
`/api/projects/{projectRef}/agent/activity`
(`specs/agent-path-amplification/spec.md:3,19`) and explicitly requires the
unscoped routes to remain absent (`tasks.json:90`). Current code confirms that
the project-scoped routes are the active product surface
(`packages/server/src/Mohist.Server/Api/AgentRoutes.cs:15-39`) and that
`/api/agent/status` is intentionally pinned to 404
(`packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/RuntimeEntrySpecs.cs:359-365`).

The artifacts therefore silently reinterpret a literal issue acceptance
criterion. Before build, the issue or all plan artifacts must agree on one
route contract; an implementer cannot satisfy both.

### F2 - Default-off recording makes agent amplification values untrustworthy (high)

The design activates request recording only when `Mohist:Otel:Enabled` is true
(`design.md:54,96`), and T-003 repeats that gate (`tasks.json:61`). The collector
option currently defaults to false (`packages/server/src/Mohist.Server/Otel/OtelOptions.cs:51-57`),
and the plan deliberately keeps that default until #472 (`design.md:13`,
`tasks.json:30`). T-004 nevertheless requires every agent response to contain
actual `databaseCalls` and `downstreamCalls` (`tasks.json:87-94`) without
conditioning those values on observability being enabled.

Under the planned default, the agent endpoints still execute database and grain
work but have no active request scope, so their fixed amplification object can
only report false zeros or depend on an undefined fallback. Request-local work
accounting for these product responses must be available independently of
Meter/route recording, or the public contract must explicitly define disabled
semantics.

### F3 - The bounded storage probe has no implementable cancellation boundary (high)

Design D5 promises one in-flight sample, a fixed per-probe cancellation budget,
and shutdown that cancels and awaits the active sample (`design.md:131-138`).
T-001 requires single-flight/coalescing but does not name or test the fixed
probe budget (`tasks.json:17`). More importantly, the current OTel database
open/bootstrap/schema path is synchronous and accepts no cancellation token
(`packages/server/src/Mohist.Server/Otel/OtelDb.cs:207-244`, with synchronous
commands continuing below line 247). A timer can stop awaiting that work, but
cannot thereby stop it; doing so would either leave an orphan probe or violate
the one-in-flight and clean-shutdown guarantees.

The design must identify a genuinely interruptible/bounded production probe
and its timeout behavior, or weaken the guarantee consistently in design,
tasks, and tests. A fake that honors cancellation is insufficient evidence for
the real adapter.

### F4 - Route selection and ordering are underspecified (high)

Proposal/spec language calls the result "anomalous routes"
(`proposal.md:8`; `specs/runtime-observability-metrics/spec.md:40`) but defines
no anomaly threshold or qualification rule. D4 instead ranks every observed
route and takes 10 (`design.md:114-123`), which can return normal routes. The
plan must either define anomaly qualification or consistently call this a
ranked top-route summary.

The selected ordering is also incomplete. D4 and T-003 sort by combined
database/downstream calls per request, then average duration
(`design.md:114`; `tasks.json:66`), while T-003 demands deterministic tie tests
(`tasks.json:68`). Equal values have no final key and therefore inherit map or
observation order. The contract also only implies, rather than states, equal
weight for one database and one downstream call. A final stable tie-breaker and
the ranking formula must be explicit before tests can lock behavior.

### F5 - Rejected telemetry has no current end-to-end producer (medium)

The issue and status spec require rejected telemetry to be visible and to make
status degraded (`specs/otel-runtime-status/spec.md:3,22-26,38-42`). D1 defines
the counter, but reserves `Rejected` for a future non-retryable partial-success
path (`design.md:52`). Migration step 3 and T-001 explicitly defer that producer
to #437/#471 (`design.md:242`; `tasks.json:16,30`).

After issue #470 alone, the real receiver can publish saved/dropped/write-failed
outcomes but cannot exercise a rejected outcome through its ingestion boundary.
The plan must clarify whether this issue promises only a tested publication
contract for dependent issues or an end-to-end rejection behavior; the current
spec/task combination says both.

### F6 - Bind-fallback shutdown ordering is inconsistent (medium)

D6 says the failed app is disposed before constructing the alternate host
(`design.md:156`). T-002 correctly strengthens this to stopped and disposed
(`tasks.json:38`). Current `Program.cs` does neither before building a second
host (`packages/server/src/Mohist.Server/Program.cs:86-92`), even though both
hosts configure Orleans (`Program.cs:102-130`). On partial `StartAsync` failure,
disposal alone is not an explicit guarantee that all started hosted services
have completed `StopAsync` before a replacement silo starts.

Design and task must state the same exception-safe sequence: stop the partially
started host, dispose it, and only then construct/start the alternate host,
including what happens if stop itself fails.

### F7 - Request-scope and autonomous-task verification gaps remain (medium)

The design claims caller-side, non-transitive Orleans accounting through an
ambient `AsyncLocal` (`design.md:96-104`), but T-003 asks only for generic
adapter/parallel tests (`tasks.json:61-68`). In the co-hosted silo, that boundary
must be verified through an actual in-process Orleans call chain; otherwise
execution-context flow can attribute grain-to-grain work transitively and
inflate counts contrary to D3.

T-004 also needs an immutable request-scope snapshot before middleware
completion (`design.md:211-217`). T-003 mentions that output only in notes
(`tasks.json:79`), not in acceptance criteria, so the dependency contract is not
machine-verifiable. Finally, T-001 still spans the Meter catalog, ingestion
outcome redesign, sampler, degradation state machine, status API, CLI, startup
wiring, and cross-package tests (`tasks.json:9-30`). That concentration leaves
all later work blocked on one large multi-failure-domain task. The graph should
either split this vertical further at a usable boundary or make the internal
delivery/checkpoints explicit enough for reliable autonomous execution.

## Coverage And Structure

- The proposal lists three capabilities and each has a corresponding spec.
- The specs contain 14 requirements and 31 correctly formed `#### Scenario:`
  blocks; every requirement has scenarios.
- `tasks.json` is valid JSON with `passes=false` on all four tasks. Its DAG is
  valid: `T-001 -> {T-002, T-003} -> T-004`, and dependencies point to lower
  priorities.
- The plan does cover low-cardinality label tests, bounded route memory, status
  no-scan behavior, transition-only logging, self-feedback exclusion, fake
  time, and `/api/health` independence. Those strengths do not resolve F1-F7.

<promise>FAIL</promise>
