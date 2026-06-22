# Self Review Report

## Result: PASS

The four plan artifacts (proposal, spec, design, tasks) are internally consistent, fully cover the issue's 11 acceptance criteria and 7 non-goals, respect the issue's explicit "five layers are one chain" framing, and form a sound acyclic 2-task DAG with appropriate (non-over-fine) granularity. One acceptance criterion was reworded for precision; two non-blocking items are deferred to build-time verification already structured into the design.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `tasks.json` T-001 acceptance criterion 5 was internally contradictory — it said "a test confirms spans flow to the configured endpoint by swapping in an in-memory exporter that records what would have been exported." You cannot both flow to the real endpoint and swap in an in-memory exporter. The wording conflated two distinct verifications (exporter-option configuration vs. pipeline delivery) that design Decision 6 keeps separate and intentionally decoupled from live OTLP transport.
  Verification: Reworded to: "a test asserts the exporter options carry the configured protocol and endpoint, and that the export pipeline delivers spans (verified by registering an in-memory capturing processor that records the spans the processor would export)." `tasks.json` re-validated as JSON after the edit. Intent and scope unchanged; aligns with design Decision 6.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Spec requirement "SignalR hub method invocations are traced as child spans" and issue AC #2 assert that a SignalR hub method appears as a child span in the same trace as an inbound HTTP request (`与 HTTP span 同属一条 trace，parent-child 正确`). The plan faithfully encodes this. However, the `Microsoft.AspNetCore.SignalR.Server` ActivitySource traces inbound hub method invocations that occur on the connection's own read loop, not inside an HTTP request pipeline. For client-initiated hub calls (the common runner/web pattern), there is typically no ambient HTTP activity to parent to, so the hub-method span may be a root span rather than a child of an HTTP request. Whether the HTTP→SignalR parent-child link the issue assumes actually holds depends on Mohist's dispatch path and is not determinable from plan artifacts alone. T-002's acceptance criterion is worded correctly ("parent is the active activity context of the carrying connection") and does not over-claim HTTP parentage, but spec Req 2's scenario ("the span SHALL belong to the same trace as the inbound request that established the interaction") and spec Req 6's full-chain scenario implicitly assume the linkage holds.
  SuggestedAction: During T-002 build, verify the actual SignalR→HTTP parent-child behavior with the in-memory exporter across Mohist's real hub invocation paths. If the linkage does not hold for client-initiated calls, surface it as a build-stage finding and, if needed, propose an issue/spec refinement — do not unilaterally weaken the spec away from the issue's stated requirement during plan self-review.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Two cross-issue / empirical verifications are correctly structured into the design (Open Questions) and tasks but cannot be closed at plan time: (a) the OTLP exporter appending `/v1/traces` to the default `http://localhost:4318/otel` must land on #219's actual ingest route once both changes share a branch; (b) the exact Orleans 10 ActivitySource name(s) (expected `Orleans.Runtime`) must be captured empirically. Both are non-blocking for plan correctness but are hard dependencies for build success.
  SuggestedAction: Resolve both during the build stage as already prescribed in design Open Questions and T-002 acceptance criteria ("record every emitted source name"; "confirm against #219's routing once both changes share a branch").
  Status: follow-up

<promise>PASS</promise>
