# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the repository's architecture, current OpenCode Workflow path, and testing rules. This review does not modify the plan artifacts.

## Findings

### F-1 High: The design permits permanent loss of Session audit facts that the issue and spec require

The issue requires assistant text, tool calls, and usage to be visible in the Session page. `specs/pi-workflow-session/spec.md:98-111` consequently requires every Pi Workflow turn to report those facts and makes duplicate delivery idempotent. However, `design.md:142-144` says a post-admission event transport failure is only logged and explicitly accepts an audit gap; `tasks.json:90` carries the same lossy behavior into T-004.

This allows a task to succeed while permanently missing the exact transcript/tool/usage evidence in the acceptance criteria. The plan must define a durable, idempotent delivery or later reconciliation path that does not replay the Prompt, and T-004 must test pre-admission input failure, post-admission transport failure, ambiguous delivery, retry/dedup, and bounded drain behavior. If observability is intentionally best-effort, the issue/spec contract would have to change instead; the current artifacts cannot promise both.

### F-2 High: An unconfirmed abort can release the queue while the prior turn is still running

`specs/pi-workflow-session/spec.md:83-96` forbids overlapping Workflow turns on one logical AgentSession. `design.md:111-115` releases the keyed queue when the operation settles, while `design.md:119-125` and `specs/pi-runtime/spec.md:93-113` allow the operation to settle with an interruption-unconfirmed result when Pi cannot be proven stopped.

Nothing then quarantines the physical Session or blocks the next queued task, so the next Prompt can start while the previous turn may still execute. The design must define the post-unconfirmed-abort admission state: retain the lease until stop is observed, quarantine the binding, or reject later work with an actionable recovery path. The selected behavior needs deterministic concurrency tests in T-002/T-004.

### F-3 High: T-003's required Workflow Session wire change would break the existing OpenCode Action

T-003 independently makes `runtime` and expected-current binding fields required (`tasks.json:55-76`). The current OpenCode Action sends neither runtime on open nor expected runtime/session on attach (`packages/runner/src/actions/opencode.ts:96-108,135-145`). T-003 does not require migrating that caller, and T-004's OpenCode-preservation criteria do not close the gap.

T-003 therefore cannot be delivered independently without breaking the only working Workflow Inline Agent path. The task must either migrate and regression-test `mohist/opencode` atomically with the Server contract, or keep the new wire fields additive until all callers switch. Since the binding command is runtime-neutral, coverage must also prove a guarded Pi-to-OpenCode rebind, not only another-runtime-to-Pi.

### F-4 Medium: Pi parent-context injection conflicts with the architecture-owned dispatch specification

`design.md:75-81` and T-004 (`tasks.json:94`) require plan-stage parent issue context to include `mohist/pi`. The authoritative dispatch design currently says the context is attached only when `uses = mohist/opencode`, and excludes every other Action (`design/workflow/task-dispatch.md:65-75`). `design/architecture.md:33-35` makes `design/` authoritative over OpenSpec for architecture rules.

If Pi is intended to receive the same parent context, the canonical dispatch design must be revised before implementation and that revision must be explicit in a task. Otherwise the Pi parent-context change must be removed from this plan.

### F-5 Medium: The `session` input's accepted and invalid forms are undefined

`specs/pi-workflow-action/spec.md:17-43` precisely defines prompt/options/model/variant validation but does not define the type, whitespace, empty string, or null behavior of `session`. `design.md:65-79` only says it is validated, and T-004 has no acceptance matrix for it. This matters because the current helper stringifies non-string values (`packages/runner/src/core/json.ts:24-28`) and can turn an object or number into a durable Session name.

The spec must state which `session` values mean omitted/default-to-Work-ID and which fail `invalid-input`; design and task tests must pin the same behavior before Session identities are persisted.

### F-6 Medium: The promised Runner-configurable provider policy has no configuration path

`specs/pi-runtime/spec.md:115-135` requires a Runner-configured retry threshold, and `design.md:121-127` describes configurable provider patterns/threshold behavior. T-002 tests only defaults and threshold mechanics (`tasks.json:40-41`); no artifact identifies the configuration source, validation/defaulting, host wiring, or a test proving a non-default value reaches `PiRuntime`.

The plan must either define and task the Runner configuration surface or narrow the normative contract to fixed defaults plus an internal test seam. Leaving "Runner-configured" undefined gives implementers no testable completion condition.

### F-7 Medium: The breaking Node floor migration omits contributor documentation

The proposal marks Node 22.19 as a breaking requirement (`proposal.md:13`). T-001 updates manifests, CI, and Docker (`tasks.json:9-17`), but `CONTRIBUTING.md:5-10` still advertises Node >=22.0.0. T-001 must include the documented development prerequisite and verify all installation instructions remain accurate; otherwise contributors can follow the repository documentation with an unsupported runtime.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All five task IDs and dependencies resolve; the graph is acyclic and every dependency points to a lower priority.
- All three proposal capabilities have matching spec files; every requirement has at least one correctly headed scenario.
- All task spec paths/anchors resolve, and the task references collectively cover all 20 requirements.
- Scope exclusions for AgentJob Pi routing, Session commands, ACP/RPC, and model-catalog UI are otherwise consistently stated.

## Verdict

The plan has three build-blocking contradictions around required audit delivery, turn serialization after unconfirmed interruption, and independent migration of the shared Workflow Session wire contract. The remaining findings leave authoritative dispatch behavior, durable Session input identity, provider-policy configuration, and the breaking toolchain migration incomplete. These must be corrected before autonomous build execution.

<promise>FAIL</promise>
