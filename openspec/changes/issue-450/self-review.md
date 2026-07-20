# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current callers of the affected wire contracts, and repository architecture/testing rules. This review modifies no plan artifact.

## Findings

### F-1 Critical: The sequenced Workflow event wire omits existing Follow-up producers

T-003 makes the Workflow runtime-event command carry a deterministic stream identity and sequence and rejects gaps or sealed streams (`tasks.json:60,68`). The current Workflow Follow-up handler and its durable failure outbox both call that same Workflow endpoint with the shared unsequenced `AgentSessionRuntimeEventsRequest` (`packages/runner/src/server/followup-handler.ts:199-220`; `packages/runner/src/server/followup-failure-outbox.ts:145-171`; `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:673-682`). T-003 migrates OpenCode Action open/bind/event behavior, while T-004 migrates Workflow Action binding streams, but neither owns these existing producers or splits the DTO/route.

Making the new fields required breaks current Workflow Follow-up reporting; leaving them optional lets Follow-up facts bypass the cursor and permits a runtime rebind to seal while an existing producer still has pending facts. This can be fixed without adding Pi Session-command routing: the plan must either split Action-owned sequenced events from existing Session-command events, or atomically migrate every caller and define how Follow-up participates in stream ordering and rebind fencing. Regression tests must cover the chosen boundary.

### F-2 High: Cleanup can lose the original physical Pi binding between turns

D6 holds the shared coordinator only through one Action's Prompt and reporter persistence (`design.md:113-119`). Worktree completion checks happen afterward, and cleanup invokes the Action a second time (`specs/pi-workflow-action/spec.md:121-129`). A queued task using the same logical Session can therefore acquire the coordinator between the successful work turn and its cleanup turn, switch runtime or binding, and release it before cleanup re-enters.

The cleanup Action would then resolve or create a different Pi binding, violating the requirement that cleanup use the original physical Pi Session without rotation. Saying cleanup uses the same coordinator key (`tasks.json:147`) does not preserve which binding is current across the gap. The plan must either keep the logical-Session lease through completion checks and any cleanup turn, or make cleanup atomically require the original runtime and physical binding and fail rather than rebind. The concurrency test must include an intervening same-name task.

### F-3 High: The plan expands Action mechanisms that the architecture target removes

D4 adds another runtime handle to `ActionContext` and adds `mohist/pi` to `PROMISE_PROJECTED_ACTIONS` plus a name-based cleanup classifier (`design.md:64-82`; `tasks.json:138-146`). The architecture-owned Action design instead requires runtime access through a declared `agent-turn` capability (`design/workflow/actions.md:52-80`), requires promise projection to dispatch by capability rather than `uses` name (`design/workflow/actions.md:140-148`), and lists runtime-bearing `ActionContext` plus `PROMISE_PROJECTED_ACTIONS` as implementation gaps to remove (`design/workflow/actions.md:288-309`).

This issue need not introduce a generic `AgentRuntime`, but it cannot claim architectural conformance while broadening two explicitly deprecated mechanisms. The plan must use the `agent-turn` capability boundary, depend on the issue that implements it, or record a deliberate temporary architecture gap with a concrete follow-up and avoid presenting the name-based expansion as the target design.

### F-4 High: Provider-failure abort has no defined unconfirmed-stop outcome

The provider requirement says a non-recoverable retry event interrupts the turn, verifies interruption, returns `turn-failed`, and preserves the binding (`specs/pi-runtime/spec.md:141-161`). Timeout and cancellation have explicit unconfirmed-stop scenarios and quarantine behavior (`specs/pi-runtime/spec.md:93-139`), but provider-triggered abort does not. D6 broadly quarantines an unconfirmed abort (`design.md:119`), while T-002 requires unconfirmed quarantine only for timeout or cancellation (`tasks.json:41-43`).

If provider abort cannot be confirmed, the implementation has no normative answer for whether the result remains `turn-failed`, changes code, or carries an unconfirmed diagnostic, nor whether both physical and logical quarantine must be established before return. Add the provider-specific scenario and task coverage so a continuing Pi retry loop cannot overlap later work.

### F-5 High: Provider credentials and smoke-artifact redaction have no acceptance owner

The proposal and runtime spec require provider credentials to remain Pi/operator-managed and prohibit Mohist from collecting, persisting, or exposing API keys (`proposal.md:8`; `specs/pi-runtime/spec.md:39-53`). T-001 records real-SDK smoke results and T-002 loads global Pi authentication (`tasks.json:9-16,31-44`), but no criterion requires redacting the smoke artifact or proves secrets cannot enter diagnostics, registration payloads, outbox manifests/events, task logs, or Action output.

This is a concrete repository-secret and data-exposure risk because the real smoke artifact is committed. T-001 must define a redacted artifact shape, and runtime/Action tests must inject sentinel credentials and prove they remain confined to the SDK authentication boundary.

### F-6 Medium: The filesystem adapter's crash-consistency promise is unverified

D8 promises a concrete temporary-write, file sync, atomic rename, and parent-directory sync protocol (`design.md:146`), then says default tests never instantiate the production adapter and TypeScript checking is sufficient (`design.md:166-174`; `tasks.json:87-98`). Type checking cannot verify operation order or failure semantics, especially rename-success followed by directory-sync failure. Yet the durable outbox guarantee relies on those exact details.

The plan needs a repository-compliant verification method: keep physical filesystem access out of default tests, but move the atomic-replace decision sequence behind a pure or injected low-level operation boundary that can be driven with in-memory fakes, and state the supported platform/filesystem assumptions. The physical composition adapter itself can remain uninstantiated.

### F-7 Medium: Corrupt outbox state has an ambiguous readiness blast radius

D8 first says an unreadable committed snapshot makes Session reporting unavailable (`design.md:146`), then says an unavailable or corrupt store leaves "reporting not-ready" (`design.md:154`). The normative scenario blocks only the affected Session (`specs/pi-workflow-session/spec.md:165-169`), while D3's combined readiness gate can stop all OpenCode, Pi, and AgentJob claims (`design.md:54-62`). T-004 also does not say whether its reporting-unavailable diagnostic is per stream or global (`tasks.json:95-96`).

Specify one authority and blast radius. A corrupt single stream should either quarantine only its logical Session while unrelated streams and work remain available, or intentionally fail global Runner readiness; tests must pin the selected behavior and the host gate must consume it consistently.

### F-8 Medium: High-risk acceptance coverage remains incomplete

Several normative boundaries are assigned implementation criteria but no focused regression tests. T-006 owns pre-registration startup, combined readiness, periodic retry, polling suppression, and continued `awaitingAck` drain (`tasks.json:138`), but its test matrix omits those host behaviors and fake-timer retry coverage (`tasks.json:148`). D10 requires Action error-mapping tests (`design.md:168-174`), while T-006 explicitly exercises only timeout, cancellation, provider, and reporting mappings; it omits `runtime-unavailable`, `runtime-session-missing`, `session-workspace-mismatch`, `session-binding-failed`, and `incompatible-runtime`, including bind-failure-before-Prompt (`tasks.json:142-148`).

Server coverage also omits the dedicated invariant that Session final/end facts cannot complete TaskRun or advance Workflow (`specs/pi-workflow-session/spec.md:195-199`), and projection coverage does not name the required input/output/cache/thought usage dimensions (`specs/pi-workflow-session/spec.md:124`; `tasks.json:40,91,171`). Add explicit acceptance tests at the owning task boundaries; these are high-risk control-flow and audit contracts, not incidental unit detail.

### F-9 Medium: Same-name context continuity is stated unconditionally across a context-breaking runtime switch

The Session requirement says same-name tasks share one logical conversation and the second receives the first task's conversation context (`specs/pi-workflow-session/spec.md:1-9`). The same spec says switching runtimes creates a new physical binding without migrating old context (`specs/pi-workflow-session/spec.md:33-65`). The canonical product document similarly pairs same-name context sharing with backend rotation (`docs/actions/pi.md:65-88`).

Logical AgentSession identity and physical conversation continuity are different guarantees. Qualify the first requirement and product wording: same-name tasks retain conversation context only while they use the same current physical binding; a runtime switch preserves logical identity and lineage but intentionally starts empty context.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic and every dependency points to a lower priority.
- All task spec paths and requirement anchors resolve.
- All three proposal capabilities have matching spec files; 21 requirements contain 73 correctly headed scenarios.
- The stated exclusions for Pi AgentJob routing, Pi Session commands, ACP/RPC, and runtime-aware model-catalog UI are otherwise consistent.

## Verdict

The previous eight findings are largely addressed, but the revised plan exposes a breaking migration gap for existing Workflow Follow-up event producers and leaves cleanup binding continuity, Action architecture conformance, provider-abort safety, credential handling, durable-adapter verification, readiness scope, and several high-risk tests unresolved. These issues must be corrected before build execution.

<promise>FAIL</promise>
