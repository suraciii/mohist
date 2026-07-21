### Requirement: Pi is a pinned in-process Runner capability

The Runner SHALL bundle one exact Pi SDK version and execute it in process behind Mohist-owned runtime types. The repository and Runner Node engine SHALL be at least the SDK-required `22.19`, and Pi SDK imports SHALL remain inside `runtime/pi`.

#### Scenario: Runner installation includes Pi

- **WHEN** the Runner is installed from the repository lockfile on a supported Node release
- **THEN** no separate Pi CLI, ACP bridge, or RPC service is required
- **AND** callers cannot import Pi SDK types outside the Pi runtime module

#### Scenario: SDK drift is resolved before implementation

- **WHEN** T-001 begins
- **THEN** a real-SDK smoke verifies the construction, trust, Session, prompt, message, event, model, thinking, steer, abort, stop-confirmation, and literal-prompt surfaces
- **AND** the artifact records sanitized structure rather than credentials or message content
- **AND** any drift is reconciled into `design/runtimes/pi.md` before runtime product code proceeds

### Requirement: Pi readiness gates new Runner work

Pi SHALL join the existing Runner readiness gate after SDK services and model catalog loading complete.

#### Scenario: Initialization enables polling

- **WHEN** SDK services initialize and catalog loading succeeds
- **THEN** Pi reports ready and the Runner may claim new work if its other readiness conditions also pass

#### Scenario: Empty catalog remains ready

- **WHEN** catalog loading succeeds with no configured models
- **THEN** Pi reports ready with a warning because model validity remains a turn-time Pi decision

#### Scenario: Initialization failure pauses polling

- **WHEN** SDK initialization or catalog loading fails
- **THEN** Pi remains unavailable with an actionable diagnostic
- **AND** the Runner retries initialization without claiming new work
- **AND** already acknowledged work continues through the existing drain path

### Requirement: Repository-local Pi execution configuration is untrusted

PiRuntime SHALL fix project trust to false. Runner-user global Pi configuration and authentication MAY load, and repository instruction files MAY provide model context, but repository `.pi/` execution resources SHALL NOT load.

#### Scenario: Repository Pi resources cannot alter execution

- **GIVEN** a worktree contains project settings, extensions, packages, skills, or prompts under `.pi/`
- **WHEN** PiRuntime constructs its services
- **THEN** those resources are absent from the effective runtime configuration
- **AND** callers cannot override the trust policy

#### Scenario: Repository instructions remain context

- **GIVEN** a worktree contains `AGENTS.md` or `CLAUDE.md`
- **WHEN** Pi prepares model context
- **THEN** those instruction files remain available independently of project trust

### Requirement: Pi tools execute unattended

PiRuntime SHALL configure and invoke Pi so an allowed tool call does not wait for per-tool human approval.

#### Scenario: Tool loop completes without approval interaction

- **WHEN** a fake-backed turn emits and completes a tool call
- **THEN** the runtime reaches prompt completion without an approval callback or confirmation state
- **AND** the SDK smoke verifies the same headless behavior on the pinned integration surface

### Requirement: Provider credentials remain inside the Pi boundary

Pi authentication SHALL remain operator-managed through Pi. Mohist-owned requests, results, runtime events, registration, Action output, and committed smoke evidence SHALL NOT carry credential values, and SDK/provider text SHALL pass the existing credential masker before Mohist text sinks.

#### Scenario: Sentinel credential is confined

- **GIVEN** the fake SDK authentication boundary receives a sentinel credential
- **WHEN** readiness, a successful turn, and a provider failure are exercised
- **THEN** the sentinel is absent from diagnostics, task logs, runtime events, registration, Action output, and smoke-shaped evidence

#### Scenario: Smoke evidence is sanitized

- **WHEN** the real-SDK smoke artifact is written
- **THEN** it contains only package/Node versions, operation names, booleans, and sanitized field/type summaries
- **AND** it contains no environment values, auth-store content, raw provider response, prompt, message text, or SDK object dump

### Requirement: Physical Pi Sessions use persisted file identity

PiRuntime SHALL expose the normalized absolute Pi session-file path as `runtimeSessionId`, cache live SDK Sessions by that path, and restore cache misses through the SDK open operation.

#### Scenario: New Session exposes a bindable path

- **WHEN** Pi creates a physical Session for a work directory
- **THEN** it returns a normalized absolute session-file path before prompt submission
- **AND** absence of that path fails as `incompatible-runtime`

#### Scenario: Existing Session is restored lazily

- **WHEN** a bound path is not present in the process cache
- **THEN** PiRuntime opens that exact path and restores its conversation, model, and thinking state

#### Scenario: Missing bound Session is not replaced

- **WHEN** the bound file is missing or corrupt
- **THEN** the turn fails as `missing-session`
- **AND** PiRuntime does not create a replacement or submit the prompt

### Requirement: A Pi turn has one completion authority

PiRuntime SHALL submit the resolved prompt literally and await `session.prompt()` as the sole turn-completion authority. It SHALL derive final assistant text from the completed Session messages.

#### Scenario: Awaited prompt completion returns final text

- **WHEN** a Pi prompt and its tool loop finish successfully
- **THEN** `runTurn` returns the final assistant text from `session.messages`
- **AND** no second wait or event-based completion decision occurs

#### Scenario: Events cannot complete a turn early

- **WHEN** an end-like event arrives before `session.prompt()` resolves
- **THEN** the runtime may project it but remains running until the prompt promise resolves or the turn fails

#### Scenario: Slash-prefixed prompt remains literal

- **WHEN** resolved Workflow input begins with `/`
- **THEN** the exact text reaches Pi without prompt-template or slash-command expansion

### Requirement: Model and thinking level apply per turn without rotation

PiRuntime SHALL apply optional model and variant choices to the current physical Session before prompting. Model identifiers SHALL split only at the first slash.

#### Scenario: Model identifier retains nested path

- **WHEN** `options.model` is `provider/family/model`
- **THEN** provider is `provider` and model ID is `family/model`

#### Scenario: Selection changes preserve physical identity

- **WHEN** a later turn changes model or variant
- **THEN** the setters run on the existing Session
- **AND** its session-file path and binding remain unchanged

#### Scenario: Omitted selection preserves Pi behavior

- **WHEN** model or variant is omitted or null
- **THEN** the restored Session choice or Pi default is used without a synthetic override

### Requirement: Workflow deadlines and interruption are deterministic

The Workflow host SHALL declare a fixed 60-minute turn duration through Runner-private context. Pi Action input SHALL NOT override it. PiRuntime SHALL use an injected clock and timer seam, issue exactly one wrap-up steer at minute 55 while the turn remains active, fix the failure result before aborting, and never replay an uncertain prompt.

#### Scenario: Deadline fixes timeout before abort

- **WHEN** the declared deadline expires while the prompt is running
- **THEN** the result becomes `deadline-exceeded` before `session.abort()` is called
- **AND** late prompt completion cannot turn it into success

#### Scenario: External cancellation fixes interruption before abort

- **WHEN** the host AbortSignal fires first
- **THEN** the result becomes `interrupted` before abort cleanup
- **AND** inability to confirm stop is exposed as a diagnostic rather than claimed as a safe stop

#### Scenario: Workflow wrap-up occurs at minute 55

- **WHEN** a Workflow Pi turn remains active for 55 minutes
- **THEN** one runtime-neutral warning is sent through `steer` at that boundary
- **AND** no warning is sent before minute 55 or after an earlier completion
- **AND** fake-clock tests advance the injected clock and timer rather than waiting on wall time

#### Scenario: A short declared duration warns immediately

- **WHEN** another PiRuntime caller declares a duration of five minutes or less
- **THEN** one wrap-up warning is sent after the turn begins
- **AND** no second warning is sent before its deadline

#### Scenario: Action input cannot override duration

- **WHEN** a Workflow author supplies an unknown timeout-like input
- **THEN** it does not change the Runner-private 60-minute Pi duration

#### Scenario: Crash-window redelivery is not hidden

- **WHEN** the Runner dies after prompt submission but before work completion is recorded
- **THEN** PiRuntime stores no replay token and does not replay the prompt itself
- **AND** normal Workflow redelivery may execute a duplicate turn

### Requirement: Provider exhaustion ends a turn promptly

PiRuntime SHALL consume the same runtime-neutral pure provider-failure policy as OpenCode. Its defaults SHALL cover quota, credit, billing, and usage-limit messages with consecutive-retry threshold 5. The Runner composition root SHALL parse optional Runner-scoped additional patterns and threshold once at startup, validate them, and pass one frozen policy instance to both runtimes. Tests MAY inject a policy directly; Action input SHALL NOT configure it.

#### Scenario: Exhausted quota fails immediately

- **WHEN** a retry event reports a configured quota or billing exhaustion message
- **THEN** PiRuntime aborts the turn and returns `turn-failed` without waiting for another retry
- **AND** the physical binding remains unchanged

#### Scenario: Transient retry remains below threshold

- **WHEN** a recoverable retry attempt is below the configured threshold
- **THEN** Pi remains authoritative for retrying and Mohist does not fail the turn yet

#### Scenario: Repeated retry reaches threshold

- **WHEN** the event-reported attempt reaches the configured threshold before completion
- **THEN** PiRuntime aborts and returns `turn-failed`

#### Scenario: Non-default Runner policy reaches both runtimes

- **WHEN** `MOHIST_PROVIDER_ERROR_PATTERNS` contains a valid JSON array of regex sources or `MOHIST_PROVIDER_RETRY_THRESHOLD` contains a positive integer
- **THEN** OpenCode and Pi receive the same policy object
- **AND** configured patterns append the defaults while the configured threshold replaces default 5
- **AND** Action input cannot observe or override the policy

#### Scenario: Invalid Runner policy is rejected

- **WHEN** either Runner setting contains invalid JSON, an invalid regex source, or a non-positive threshold
- **THEN** Runner startup fails before work claim with an actionable diagnostic rather than silently changing defaults

### Requirement: Pi event projection is normalized and idempotent

PiRuntime SHALL normalize assistant text, reasoning, tool lifecycle/results, model observations, status, automatic compaction, provider retry, and usage including input, output, cache read, cache write, thought when supplied, cost amount, and currency. Unknown SDK events SHALL affect diagnostics only.

#### Scenario: Duplicate callback does not duplicate a fact

- **WHEN** Pi repeats a message or tool callback with the same stable identifier
- **THEN** the projector emits that logical fact once

#### Scenario: Final messages reconcile callbacks

- **WHEN** prompt completion exposes a final assistant message or completed tool fact absent from earlier callbacks
- **THEN** the projector emits the missing normalized fact before returning

#### Scenario: Automatic compaction remains an audit fact

- **WHEN** Pi emits `compaction_start` and `compaction_end`
- **THEN** the projector emits ordered `compaction_event` facts with started/completed phases
- **AND** those facts do not complete or fail Workflow work

#### Scenario: Provider retries remain audit facts

- **WHEN** Pi emits `auto_retry_start` or `auto_retry_end`
- **THEN** the projector emits a masked `provider.retry` status fact containing its phase and structured attempt fields
- **AND** the same source event may inform provider-failure policy without the audit fact becoming a completion signal

### Requirement: Default tests isolate the Pi SDK and external environment

Runtime unit and product-path specs SHALL use injected SDK services, virtual Session paths, fake Server connections, and fake clocks.

#### Scenario: Default verification is deterministic

- **WHEN** Runner and repository tests execute
- **THEN** no new test contacts a real provider or network, spawns a real Pi process, reads a user's Pi configuration, depends on a real Session store, or waits on wall time
