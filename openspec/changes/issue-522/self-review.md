# Self Review: Issue 522 Plan

## Findings

### 1. [Blocking] Follow-up Turn lifecycle drivers are unspecified — stop cannot apply to a follow-up Turn

D1 introduces durable `AgentTurnRecord`s for follow-up idle-start Turns (recorded as `Queued`), and D2 generalizes the transition *methods* to be Turn-id-keyed. But neither the design nor T-001 specifies **what drives those Turns from `Queued → Executing → terminal` for the non-launch case**, and today nothing does.

Evidence in current code:
- `MarkInitialTurnExecuting` / `MarkInitialTurnTerminal` are called **only** from `AgentJobGrain` (`AgentJobGrain.cs:235,362,810,1324,1335,1350`). The AgentJob is the sole driver of the launch Turn's Executing/terminal transitions.
- Runtime events do not touch Turn status: `ApplyRuntimeEventToDomain` (`AgentSessionGrain.cs:1498-1532`) maps `session.input`/`session.activity` to `SetActivity` only — it never marks a Turn Executing or terminal.

Consequence for the plan: cancel/stop eligibility is Turn-status-based (design D4/D5: "Stop applies only to an Executing Turn"; spec `agent-turn-stop`: "SHALL apply only to a Turn that is executing"). After D1, a follow-up Turn is recorded `Queued` and **nothing ever marks it `Executing`**, so:
- Stop can never apply to a follow-up Turn — its premise (an Executing follow-up Turn) is never produced by its dependency T-001. T-003's central acceptance criterion ("stop request for an Executing Turn") is therefore unreachable for the follow-up case, which is exactly the case the issue cares about ("正在执行的工作").
- Normal completion of a follow-up Turn would also leave the Turn non-terminal (activity converges, Turn status does not).

Recommended fix (for the fix task, not applied here): add a decision — or extend D1/D2 — that specifies the event-driven lifecycle for non-launch Turns: which runtime event / runner report marks a follow-up Turn `Queued → Executing`, which marks it terminal (`completed`/`failed`) on normal completion, and how the stop path marks it terminal (`unknown`/terminal) on stop. Move that wiring into T-001's scope and acceptance criteria, and have T-003 drive the follow-up Turn to terminal on stop. The alternative is to explicitly descope to launch-Turn-only cancel/stop and drop D1, but that does not satisfy the issue's intent for follow-up Turns.

### 2. [Precision] D5 enumerates a `Stopped` Turn status that does not exist

Design D5's stale-guard lists terminal Turn statuses as `Completed|Failed|Cancelled|Stopped|Unknown`. The actual `AgentTurnStatus` enum is `Queued, Executing, Completed, Failed, Unknown, Cancelled` (`AgentSession.cs:387-395`) — there is no `Stopped` value. "stopped" is a stop-reply *label* (D6), not a Turn status; a stopped Turn's status is driven through the existing Completed/Failed/Unknown mapping.

Recommended fix: correct the D5 enumeration to the real terminal statuses (`Completed|Failed|Cancelled|Unknown`) and keep `stopped`/`stop-requested` as reply labels only, so an implementer does not add a bogus `Stopped` enum value.

### 3. [Minor] Command-surface docs are not updated

`design/cli.md:77` and `docs/cli-reference.md` list the session command surface as `transcript/followup/compact/reset/cancel`. D6 adds `session stop` and changes `cancel`'s semantics (BREAKING), but neither the design migration plan nor T-004 requires updating these docs. Per the AGENTS.md spec-first rule (landing a command updates `design/` and `docs/`), T-004 should carry a doc-update acceptance criterion for `design/cli.md` and `docs/cli-reference.md`.

## Verdict

Finding 1 is a build-blocking coherence gap: the substrate task (T-001) does not deliver the Executing/terminal lifecycle that the stop task (T-003) and the issue's follow-up-Turn scenarios depend on. The plan is not ready to build until that lifecycle is specified and tasked.

<promise>FAIL</promise>
