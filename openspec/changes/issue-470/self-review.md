# Self-Review - Issue 470

Reviewed the current `proposal.md`, `design.md`, `tasks.json`, and all three
capability specs against the live issue #470 details, the current Server/CLI
implementation, and the repository testing constraints.

## Verdict

The plan is not ready to build. Issue coverage, bounded-state design, route
ranking, agent aliases, off-state measurement gating, and the seven-task graph
are coherent. Two sampler lifecycle gaps can still violate startup and fallback
behavior, and four implementation/task contracts remain ambiguous or
contradictory.

## Findings

### F1 - Initial process availability is coupled to a potentially blocking storage probe (high)

The off-state spec requires working-set and GC-heap values whenever the Server
is reachable (`specs/otel-runtime-status/spec.md:5-12`). D5 puts the immediate
process sample and synchronous storage probe in the same serialized sampler
iteration (`design.md:130-139`). It does not define whether startup waits for
that iteration.

If startup does not wait, the status route can become reachable before the
required process values or a `process_read_failed` reason exists. If startup
does wait, enabled startup can be held indefinitely by a host filesystem
metadata call, despite OTel degradation being independent of core health. The
plan must separate a failure-contained initial process publication from
asynchronous storage probing and state exactly which step completes before the
Server becomes reachable. Process and storage exceptions must be isolated so a
storage failure cannot prevent process publication.

### F2 - Bind fallback can execute the "single" immediate storage probe twice (high)

T-003 requires exactly one enabled-state immediate storage probe
(`tasks.json:56`). The current startup attempts `app.StartAsync()` and only then
classifies the OTLP Kestrel bind failure
(`packages/server/src/Mohist.Server/Program.cs:77-92`). Hosted services can
therefore start and probe on the failed primary host before T-006 stops it and
constructs an alternate host, whose sampler probes again.

The plan must defer storage probing until the host has successfully reached the
application-started boundary, or explicitly redefine and bound probe ownership
across failed host attempts. T-006's fallback test must assert total sampler and
probe starts across both host graphs, not only that one live sampler remains
after fallback.

### F3 - RuntimeObservability cannot safely publish process samples (high)

D1 says the singleton's public fact methods are `CompleteRequest`,
`RecordAgentPath`, `RecordIngest`, `PublishStorage`, generic `SetDegradation`, and
`GetSnapshot` (`design.md:40-50`). There is no process publication method, yet
T-003 must atomically publish process values, invalidate them on failure,
activate `process_read`, and clear only that source on recovery
(`tasks.json:58-61`).

Generic public `SetDegradation(source, reason?)` can toggle a reason but cannot
publish the process sample, and it allows any caller to mutate another
producer's source, weakening D6's source-isolation invariant. Define a
process-owned success/failure publication contract that updates values and
`process_read` atomically. The protection and storage publishers should likewise
use narrow source-owned operations; generic source mutation should remain
internal.

### F4 - T-004 gives contradictory scope-creation criteria (medium)

T-004 says middleware creates a scope whenever OTel is enabled
(`tasks.json:79`), but later says OTel endpoints create no scope
(`tasks.json:84`). D3 contains the intended exception for `/otel/v1` and
`/otel/api` (`design.md:96`). The first acceptance criterion must include that
exception; otherwise an implementation cannot satisfy both task assertions
literally, and the feedback-loop test has no single expected behavior.

### F5 - Production storage recovery is not acceptance-locked (medium)

D5 requires a successful probe after failure to clear only `storage_read` and a
successful write after failure to clear only `storage_write`
(`design.md:139`). T-003 covers storage success followed by failure but not
failure followed by recovery (`tasks.json:59`). T-001 covers write-failure
publication and generic source isolation but does not require the real ingest
success path to clear `storage_write` (`tasks.json:10-16`).

Generic state-machine tests do not prove that production probe/write adapters
call the correct recovery operation. Add acceptance tests for read
failure-to-success and write failure-to-success, including the case where an
unrelated source remains active and must not be cleared.

### F6 - Compatibility alias precedence is undefined for a blank query value (medium)

The agent specs say `projectId` query takes precedence over
`X-Mohist-Project`, then require 400 when neither is present
(`specs/agent-path-amplification/spec.md:3,25`). They do not define whether
`?projectId=` is absent and falls back to a valid header, or is the selected
blank value and returns 400. Existing route code uses both styles of nullable
and whitespace handling, so an implementer cannot infer one repository-wide
rule.

Specify blank/whitespace query and header semantics and add alias tests for an
empty query with a valid header, whitespace-only selectors, and query/header
conflict. Both aliases must use the same rule.

## Coverage And Structure

- The proposal's three capabilities each have a corresponding spec.
- The specs contain 14 requirements and 38 correctly formed scenarios.
- `tasks.json` is valid JSON; all seven tasks have `passes=false`, all spec
  anchors resolve, and the dependency graph is acyclic with increasing
  priorities.
- The plan consistently covers tri-state status, low-cardinality labels,
  bounded route memory and response size, deterministic route ranking,
  transition-only logs, status no-scan behavior, feedback exclusion, truthful
  off-state agent counters, safe failed-host disposal, and core-health
  independence. Those strengths do not resolve F1-F6.

<promise>FAIL</promise>
