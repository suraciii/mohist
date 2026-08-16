### Requirement: Detect missing reply action attempts only for eligible Slack turns
The Runner SHALL apply the reply guard only to a turn with a valid Slack execution context and reply anchor. The guard SHALL cover both initial Agent work and Slack follow-up turns executed through the Pi or OpenCode runtime.

At the actual terminal point of an eligible turn, the Runner SHALL determine whether the existing Agent reply action was invoked during that turn. The first invocation attempt SHALL count immediately when the corresponding normalized `tool_call.started` observation is received. The later action result is irrelevant to this predicate: accepted, rejected, interrupted, or non-zero reply-action calls all count as attempts.

The Runner MUST NOT query the Server, Slack outbox, provider delivery state, or liveness state to determine whether an attempt occurred. Final assistant text, other tool output, runtime terminal facts, or liveness status alone MUST NOT count as a reply-action attempt.

#### Scenario: Initial Pi Slack work ends without a reply action attempt
- **WHEN** an initial AgentJob turn runs through Pi with a valid Slack execution context and reaches its terminal point without a Runner-observed reply action invocation
- **THEN** the Runner SHALL make the turn eligible for a reply-guard advisory

#### Scenario: Initial OpenCode Slack work ends without a reply action attempt
- **WHEN** an initial AgentJob turn runs through OpenCode with a valid Slack execution context and reaches its terminal point without a Runner-observed reply action invocation
- **THEN** the Runner SHALL make the turn eligible for a reply-guard advisory

#### Scenario: Slack Pi idle follow-up reaches terminal without a reply action attempt
- **WHEN** a Slack follow-up is admitted to an idle Pi Session, the model work reaches its terminal point after the prompt continuation completes, and no reply action invocation was observed
- **THEN** the Runner SHALL make the follow-up eligible for a reply-guard advisory

#### Scenario: Slack Pi streaming follow-up reaches terminal without a reply action attempt
- **WHEN** a Slack follow-up is injected with Pi `steer` into an already-streaming Session, the active turn later reaches its terminal point, and no reply action invocation was observed during that turn
- **THEN** the Runner SHALL make the follow-up eligible for a reply-guard advisory only after that terminal point

#### Scenario: Slack OpenCode follow-up reaches terminal without a reply action attempt
- **WHEN** a Slack follow-up runs through OpenCode and its `runTurn` completion reaches the terminal point without a reply action invocation
- **THEN** the Runner SHALL make the follow-up eligible for a reply-guard advisory

#### Scenario: A reply action call succeeds
- **WHEN** an eligible Slack turn invokes the existing reply action and the action completes successfully
- **THEN** the Runner SHALL treat the turn as having an attempted reply and SHALL NOT issue a reply-guard advisory

#### Scenario: A reply action call is rejected
- **WHEN** an eligible Slack turn invokes the existing reply action but the command returns a non-zero result or is rejected by its existing endpoint
- **THEN** the Runner SHALL still treat the turn as having an attempted reply and SHALL NOT issue a reply-guard advisory

#### Scenario: Runtime output exists without a reply action attempt
- **WHEN** a Slack-bound turn produces final assistant text, unrelated tool output, or terminal runtime facts but the reply action was not invoked
- **THEN** the Runner SHALL treat the reply as unattempted and SHALL evaluate the reply guard

### Requirement: Issue a bounded advisory using the existing reply context
For an eligible terminal turn with no reply action attempt, the Runner SHALL issue at most the configured reminder budget of advisory reminders to the same Agent session within a finite bounded wait. The default reminder budget SHALL be two. The Runner SHALL increment the reminder count before invoking each advisory and SHALL never exceed that count, including after duplicate terminal signals or late completions.

The advisory SHALL reuse the existing Slack execution context, reply anchor, and collaboration instructions already supplied to the Agent. It SHALL tell the Agent that reasoning and tool output are invisible to the Slack user and that the Agent should either publish a self-contained conclusion, evidence summary, and next step through the existing reply action or deliberately remain silent. It SHALL NOT invent reply content, select a different destination, expose reply-anchor internals, or become a Server-authored Slack message.

#### Scenario: The first advisory leads to a self-contained Agent reply
- **WHEN** the Agent has not attempted a reply at the terminal point and invokes the existing reply action during the first bounded advisory opportunity
- **THEN** the Runner SHALL record the attempt, SHALL stop pursuing further reminders, and SHALL NOT synthesize or append another reply

#### Scenario: The Agent deliberately chooses silence
- **WHEN** the Agent has not attempted a reply and chooses the silence branch after receiving an advisory
- **THEN** the Runner SHALL allow the turn to continue toward normal termination and SHALL treat the silence as a valid outcome rather than a failure

#### Scenario: A second default advisory is bounded
- **WHEN** the first advisory completes without a reply action attempt and the Agent session remains eligible for another reminder
- **THEN** the Runner SHALL issue at most one second advisory under the default budget of two and SHALL close guard processing after that advisory completes without an attempt

#### Scenario: The advisory uses the supplied reply context
- **WHEN** the Agent invokes the reply action after an advisory
- **THEN** the invocation SHALL use the existing Slack reply context and anchor supplied to the session, and the Runner SHALL not require the Agent to infer a destination from conversation history

### Requirement: Guard each turn at most once and prevent reminder loops
The Runner SHALL track guard state for each eligible turn, including whether a reply action was attempted, how many reminders were issued, and whether guard processing is closed. Repeated terminal notifications, runtime event reconciliation, duplicate delivery signals, or an unattempted reply after the reminder budget MUST NOT cause a second guard evaluation or an additional reminder beyond the configured budget.

An attempted reply observed before or during any advisory SHALL suppress all further advisory work for that turn, regardless of the action's eventual result.

#### Scenario: Duplicate terminal signals arrive for one unpublished turn
- **WHEN** the same Slack-bound turn reaches its terminal boundary more than once without a reply action attempt
- **THEN** the Runner SHALL issue no more than the configured reminder budget of two advisories and SHALL run the guard coordinator only once

#### Scenario: A reply is attempted while an advisory is in flight
- **WHEN** the Agent invokes the reply action while a bounded advisory opportunity is active
- **THEN** the Runner SHALL stop pursuing another advisory for that turn and SHALL preserve the single Agent reply attempt

#### Scenario: The reminder budget is exhausted without a reply action attempt
- **WHEN** both default advisory opportunities finish without a reply action attempt
- **THEN** the Runner SHALL close guard processing, SHALL end the turn normally, and SHALL issue no third advisory

### Requirement: Preserve the original execution outcome on guard failure or interruption
The reply guard SHALL be best effort and SHALL never change the original execution outcome. The Runner SHALL enforce a finite wait for each advisory. If an advisory times out, fails, cannot run because the runtime is unavailable, or is interrupted, the Runner SHALL retain the original success, failure, cancellation, deadline, or unknown outcome and its associated terminal reporting. The Runner MUST NOT retry the failed advisory, rerun the original turn, or convert an advisory problem into a new turn failure.

#### Scenario: Advisory times out after an otherwise successful turn
- **WHEN** an eligible Slack turn completes successfully without a reply and an advisory does not finish within its bound
- **THEN** the Runner SHALL report the original successful turn outcome and SHALL perform no advisory retry

#### Scenario: Advisory invocation fails
- **WHEN** the reply-guard advisory cannot be invoked or the runtime returns an advisory failure
- **THEN** the Runner SHALL preserve the original turn outcome and SHALL perform no Server-authored fallback reply or retry

#### Scenario: Advisory runtime is unavailable
- **WHEN** an eligible Slack turn reaches the guard boundary but the runtime needed for the advisory is unavailable
- **THEN** the Runner SHALL preserve the original turn outcome and SHALL close guard processing without retrying the turn

#### Scenario: The turn or advisory is interrupted
- **WHEN** the original Slack-bound turn is cancelled, interrupted, or reaches its deadline before the advisory can complete
- **THEN** the Runner SHALL preserve that original interrupted or deadline outcome and SHALL NOT start a replacement turn or a second advisory

### Requirement: Evaluate follow-ups at their actual terminal boundary
The Runner SHALL distinguish follow-up admission from follow-up terminal completion. A follow-up accepted by the SignalR handler, a Pi `preflight(true)` result, or a Pi `steer` result SHALL NOT by itself trigger the reply guard or terminal activity closeout.

For Pi idle follow-ups, terminal completion SHALL be observed after the background prompt continuation finishes and its terminal events are reconciled. For Pi streaming follow-ups, terminal completion SHALL be observed after the active Session turn that received `steer` becomes terminal and its events are available to the shared observation tracker. For OpenCode follow-ups, the existing `runTurn` completion SHALL be the terminal signal.

#### Scenario: A Pi streaming follow-up is admitted while the original turn is active
- **WHEN** Pi returns success from `steer` while the Session remains streaming
- **THEN** the Runner SHALL record admission only, SHALL not issue an advisory, and SHALL not emit terminal activity until the active turn reaches its terminal point

#### Scenario: A Pi idle follow-up is admitted by preflight
- **WHEN** Pi returns `preflight(true)` before the idle follow-up's prompt continuation has completed
- **THEN** the Runner SHALL record admission only and SHALL defer guard evaluation and terminal activity until prompt completion

#### Scenario: Follow-up terminal completion is observed once
- **WHEN** the runtime-specific follow-up completion handle reaches the terminal point
- **THEN** the Runner SHALL flush the terminal observer facts, run the guard at most once, and emit the existing terminal activity exactly once with its original status and output payload

### Requirement: Keep liveness and non-Slack execution independent
The reply guard SHALL use only the existing Runner Slack execution context, reply action observation, reply anchor, and runtime session. It MUST NOT extract runtime output into a Slack reply, alter terminal liveness closeout semantics, or make a missing Agent reply a Runner execution failure. Turns without a valid Slack execution context SHALL remain unchanged by the guard.

#### Scenario: A Slack turn remains silent after guard processing
- **WHEN** a Slack-bound turn ends without a reply action attempt, including after the Agent chooses silence or guard processing is unavailable
- **THEN** the existing liveness terminal state SHALL close independently and the Server SHALL enqueue no Server-authored fallback reply

#### Scenario: An Agent reply action was attempted
- **WHEN** the existing reply action was invoked and the action later succeeds, fails, or is interrupted
- **THEN** the Runner SHALL leave the existing action, liveness finalization, and original execution outcome unchanged

#### Scenario: A non-Slack turn reaches a terminal point
- **WHEN** a Pi or OpenCode turn has no valid Slack execution context
- **THEN** the Runner SHALL not issue a reply-guard advisory and SHALL preserve the turn's existing execution and reporting behavior
