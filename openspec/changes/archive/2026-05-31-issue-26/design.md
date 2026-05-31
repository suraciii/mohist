## Context

Mohist's ACP runner currently treats `agent_message_chunk` as the primary evidence of session liveness in shared, resumed, and new ACP session handling. Other ACP notifications are still emitted to Mohist and visible in the transcript, but they do not consistently update the runner liveness timestamp. This allows an actively streaming task to be failed by the liveness monitor when it is producing thought chunks, tool updates, or other protocol progress without assistant answer text.

The ephemeral ACP path already has a broader shape because it calls its data notification hook before narrowing the event type for output accumulation. The shared-session paths need the same liveness semantics while preserving the existing task output contract: assistant answer text can remain the only accumulated output artifact, but liveness must be driven by observable ACP progress.

The primary implementation area is `packages/runner/src/actions/acp-agent.ts`. The server and Web UI consume session lifecycle events, so the runner must also emit enough liveness probe metadata for live clients and stored session metadata to explain probing, recovery, and true timeout failures.

## Goals / Non-Goals

**Goals:**

- Use one shared definition of qualifying ACP liveness activity across shared, resumed, new, and ephemeral ACP paths.
- Count thought chunks, assistant message chunks, tool calls, tool updates/results, message growth, and successful protocol responses as liveness evidence when they prove the session is alive.
- Reset `lastDataAt` and satisfy an active probe when qualifying activity arrives after the active probe was recorded.
- Keep assistant answer text accumulation independent from liveness activity.
- Emit liveness status metadata that includes probe sent time, probe deadline, last qualifying activity, and probe correlation state.
- Cover the regression where a long-running shared ACP session streams `agent_thought_chunk` events without `agent_message_chunk` and must not time out.

**Non-Goals:**

- Do not change timezone rendering for activity timestamps.
- Do not change issue/session attribution behavior.
- Do not change runner concurrency, queueing, or capacity semantics.
- Do not remove liveness probes or convert them into a passive-only timeout.
- Do not broaden task output artifacts unless existing artifact contracts already require it.

## Decisions

### Decision 1: Centralize liveness activity classification

Implement a small shared classifier/helper in the ACP runner that decides whether an ACP notification or protocol response is qualifying liveness activity and returns optional evidence such as activity type and timestamp. All ACP execution paths should call this helper before type-specific output handling.

Rationale: The bug exists because liveness and output handling are coupled in some paths but not others. A central helper makes the liveness definition explicit and keeps shared, resumed, new, and ephemeral behavior aligned.

Alternatives considered:

- Update each handler inline. This is minimal in the short term but risks future drift between paths and makes the acceptance criteria harder to reason about.
- Treat every received byte/event as activity. This is simpler but may count non-progress noise or internal bookkeeping as liveness. The requirement is meaningful session activity, so the classifier should intentionally enumerate ACP progress notifications and successful protocol responses.

### Decision 2: Separate liveness activity from output accumulation

Run liveness notification for all qualifying session activity, then keep existing output accumulation limited to assistant answer chunks where that is the intended task result. `agent_thought_chunk`, tool updates, and probe responses should update liveness state but should not automatically become task output text.

Rationale: Users need accurate session health without changing the artifact contract or polluting final task output with internal thoughts/tool metadata.

Alternatives considered:

- Accumulate all visible transcript activity into task output. This would make liveness and output trivially consistent but would be a user-facing behavior change and could expose internal progress text in artifacts.
- Ignore thought/tool events for liveness if they are not output. This preserves current behavior but fails the core requirement.

### Decision 3: Correlate probes with activity versions

When the liveness monitor transitions a session to `probing`, record `probeSentAt`, `probeDeadlineAt`, and an active probe version or equivalent monotonic activity marker. Qualifying activity should store `lastDataAt`, `lastActivityType`, and the latest activity version. A probe is satisfied only when qualifying activity arrives after the recorded probe version and before the deadline.

Rationale: Timestamp-only checks can be ambiguous around event ordering and clock precision. A monotonic version makes it clear whether activity occurred after the probe was recorded, including when events arrive very close to the timeout boundary.

Alternatives considered:

- Use only `lastDataAt > probeSentAt`. This is easy but vulnerable to same-tick timestamps, clock precision issues, and unclear explanations in failure metadata.

### Decision 4: Emit explicit liveness lifecycle metadata

Emit session lifecycle events for transitions to `probing`, recovery to `running`, and `failed` outcomes using generic session liveness semantics rather than recovery-specific events. Payloads should include session identifiers, status, `lastDataAt`, `lastActivityType` when available, `probeSentAt`, `probeDeadlineAt`, and the active probe version or equivalent correlation state.

Rationale: The UI and session metadata need to explain why a session was probed, what activity satisfied a probe, or why a timeout was valid. The event should not reuse `coder_recovery_status` because this is normal liveness state, not coder recovery.

Alternatives considered:

- Only log probe state in runner logs. Logs help operators but do not satisfy live client or session metadata requirements.
- Add a bespoke timeout-only event. This explains failures but misses probing and recovery, which are needed to understand normal liveness behavior.

### Decision 5: Test through the shared ACP path

Add or update runner tests so a shared ACP session emits repeated `agent_thought_chunk` notifications without `agent_message_chunk` while crossing the previous quiet/probe window. The expected result is that liveness remains running or returns to running and no `Session liveness probe timed out` failure is produced.

Rationale: The production failure occurred in shared ACP handling, so the regression test should exercise that path rather than only the ephemeral implementation.

Alternatives considered:

- Unit-test only the classifier. This is useful but insufficient because the bug came from path integration and monitor state, not the activity list alone.

## Risks / Trade-offs

- [Risk] The classifier may count an ACP notification that does not represent real forward progress -> Mitigation: Keep the qualifying set explicit and limited to notifications/responses that prove the session is alive, and record `lastActivityType` for debugging.
- [Risk] Additional lifecycle events could increase event volume for chatty sessions -> Mitigation: Emit liveness lifecycle events on state transitions and probe satisfaction, not for every activity chunk.
- [Risk] Probe timeout behavior can become race-prone near deadlines -> Mitigation: Use a monotonic activity/probe version in addition to timestamps so post-probe activity is ordered deterministically.
- [Risk] Server or Web UI consumers may ignore new metadata initially -> Mitigation: Add fields compatibly to existing payloads and keep existing status/failure fields intact.
- [Risk] Tests with real timers may be flaky -> Mitigation: Prefer controlled/fake timers or shortened deterministic liveness thresholds in runner tests.

## Migration Plan

1. Add the shared liveness activity classifier/helper in the runner.
2. Route shared, resumed, new, and ephemeral ACP notification handling through the helper before output-specific branching.
3. Extend liveness state with probe sent/deadline, activity version, last qualifying activity timestamp, and last qualifying activity type.
4. Emit generic session liveness lifecycle payloads for probing, running recovery, and failure metadata.
5. Add regression coverage for thought-only shared ACP activity and targeted coverage for tool activity if existing test structure supports it cleanly.
6. Run runner tests and relevant full build/test checks.

Rollback strategy: revert the runner changes and tests as a single change if the new liveness metadata or classifier causes regressions. Because payload fields are additive and task output contracts remain unchanged, rollback should not require data migration.

## Open Questions

- Which existing session lifecycle event name should carry the generic liveness status if there is already a suitable event type, or should a new event type be introduced?
- What exact ACP notification names are used for tool results in the current opencode ACP client, and should any provider-specific aliases be included in the qualifying set?
- Should successful probe protocol responses count as qualifying activity even if no transcript notification follows, or only as evidence that the transport/session is alive?
