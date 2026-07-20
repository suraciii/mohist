### Requirement: Pi is a pinned in-process Runner capability

The Runner SHALL bundle a fixed version of `@earendil-works/pi-coding-agent` and SHALL execute Pi through its in-process SDK. The Runner MUST NOT require a separately installed Pi executable, MUST NOT use ACP or Pi RPC mode, and MUST NOT expose Pi SDK types outside the Pi runtime boundary. The Runner SHALL require Node.js 22.19 or newer. Before implementation proceeds, the pinned SDK's Session creation, restoration, prompting, event, model-selection, interruption, and catalog surfaces SHALL be smoke-verified against real Pi, and any observed drift SHALL be reconciled before product code relies on it.

#### Scenario: Runner installation includes Pi

- **WHEN** an operator installs a compatible Mohist Runner
- **THEN** the Runner SHALL include the pinned Pi SDK needed to execute a Pi turn
- **AND** the operator SHALL NOT need to install a Pi CLI, ACP adapter, or RPC service

#### Scenario: SDK drift is detected before implementation

- **WHEN** the pinned Pi SDK differs from the call or event surface assumed by the runtime design
- **THEN** the smoke verification SHALL record the difference
- **AND** implementation MUST NOT proceed against the stale assumption

### Requirement: Pi readiness gates Runner work claiming

Before the Runner registers or claims work, it SHALL initialize the Pi SDK services and successfully load Pi's available-model catalog. A successfully loaded empty catalog SHALL leave Pi ready and SHALL emit a warning that no credentialed provider models are available. SDK initialization failure or catalog-load failure SHALL make Pi unavailable, SHALL emit an actionable diagnostic, and SHALL prevent the Runner from claiming new work until Pi is rebuilt and readiness passes. Failure of an in-process Pi turn caused by Runner process loss MUST NOT trigger automatic prompt replay.

#### Scenario: Successful initialization enables work claiming

- **WHEN** Pi SDK services initialize and the available-model catalog loads successfully
- **THEN** Pi SHALL be ready
- **AND** the Runner SHALL be eligible to register and claim work

#### Scenario: An empty catalog is ready with a warning

- **WHEN** the available-model catalog loads successfully but contains no models backed by configured provider credentials
- **THEN** Pi SHALL remain ready
- **AND** the Runner SHALL emit a warning diagnostic without blocking work claiming

#### Scenario: Catalog failure blocks work claiming

- **WHEN** Pi SDK initialization or available-model catalog loading fails
- **THEN** the Runner SHALL stop claiming new work
- **AND** it SHALL expose an actionable Pi readiness diagnostic until initialization and catalog loading succeed

### Requirement: Repository-local Pi configuration is never trusted

The Pi runtime SHALL treat every work repository as untrusted for Pi project configuration. It MUST NOT load repository-local `.pi/` settings, extensions, packages, skills, prompts, or other project resources, and this trust decision MUST NOT be user-configurable. Runner-user global Pi configuration and provider authentication SHALL remain available. Repository `AGENTS.md` and `CLAUDE.md` instruction files SHALL remain model context and MUST NOT be treated as trusted Pi execution configuration. Mohist MUST NOT collect, persist, or expose provider API keys. Credential values SHALL remain inside Pi's authentication manager; Mohist-owned boundary types and field-whitelisted registration, event, outbox, and smoke-artifact shapes MUST NOT contain credential fields or raw SDK objects. Runtime/provider text SHALL pass through the Runner credential masker before it enters diagnostics, task logs, wire payloads, or committed smoke evidence.

#### Scenario: Repository Pi resources do not alter execution

- **WHEN** a work repository contains project-local `.pi/` settings, extensions, skills, or prompts
- **THEN** the Pi runtime SHALL ignore those resources
- **AND** the turn SHALL use the same Runner-controlled execution configuration as a repository without those resources

#### Scenario: Repository instruction files remain context

- **WHEN** a work repository contains `AGENTS.md` or `CLAUDE.md`
- **THEN** Pi SHALL provide those files as model context
- **AND** it MUST NOT treat them as permission to load project-local Pi execution resources

#### Scenario: Sentinel credentials never cross the Pi boundary

- **WHEN** fake Pi authentication and provider diagnostics contain a sentinel API key
- **THEN** the key SHALL remain absent from Mohist requests, results, registration, runtime events, outbox bytes, task logs, diagnostics, Action output, and smoke evidence
- **AND** any surfaced provider text SHALL contain only the credential mask

### Requirement: A Pi turn has one completion authority

For each Workflow turn, the Pi runtime SHALL resolve the requested physical Session, apply the optional model and thinking level to that Session, submit the resolved prompt as literal text, await the SDK prompt operation, and return the final assistant text from the completed Session. Resolution of the awaited prompt operation SHALL be the sole successful completion authority; event notifications MUST NOT complete the turn and the runtime MUST NOT issue a second wait operation. Prompt text beginning with `/` SHALL be submitted unchanged and MUST NOT trigger Pi template or slash-command expansion.

#### Scenario: Awaited prompt completion returns final text

- **WHEN** Pi completes an SDK prompt operation for a Workflow turn
- **THEN** the runtime SHALL treat that response as the turn's completion authority
- **AND** it SHALL return the final assistant text without issuing a second wait operation

#### Scenario: Events do not complete a turn

- **WHEN** an end, idle, or message event arrives before the SDK prompt operation resolves
- **THEN** the runtime MUST NOT report the turn as completed
- **AND** it SHALL continue awaiting the prompt operation or an interruption outcome

#### Scenario: Slash-prefixed prompt remains literal

- **WHEN** the resolved Workflow prompt begins with `/`
- **THEN** the runtime SHALL submit the complete text unchanged
- **AND** it MUST NOT expand the text as a Pi prompt template or slash command

### Requirement: Model and thinking level are applied per turn

An explicit model SHALL use `provider/model` form, split only at the first `/`, with the complete remainder retained as the model identifier. The Pi runtime SHALL apply `options.model` and `options.variant` as independent per-turn selections on the current physical Session; `variant` SHALL map to Pi's thinking level and MUST NOT be appended to the model identifier. Mohist SHALL leave final model and thinking-level validity to Pi. When a selection is omitted, the runtime SHALL preserve the current Session selection or use Pi's default for a new Session.

#### Scenario: Model identifiers retain additional slashes

- **WHEN** the selected model is `openrouter/vendor/family/model`
- **THEN** the runtime SHALL use `openrouter` as the provider
- **AND** it SHALL use `vendor/family/model` as the model identifier

#### Scenario: Omitted selection preserves Pi behavior

- **WHEN** a turn omits model or variant
- **THEN** the runtime SHALL preserve the corresponding current Session selection
- **AND** a new Session without a selection SHALL use Pi's default

### Requirement: Workflow deadlines interrupt Pi deterministically

A Pi Workflow turn SHALL use a 60-minute deadline declared by the Workflow executor. `mohist/pi` Action input MUST NOT expose or honor `timeout`, `deadline`, or another user-authored override in this issue. The runtime SHALL inject one task-independent wrap-up warning five minutes before the deadline. At the deadline it SHALL first fix the result as deadline exceeded, then request Pi interruption and verify whether execution stopped. A late prompt resolution MUST NOT replace the deadline result. If stopping cannot be confirmed, the runtime SHALL report interruption as unconfirmed, mark that physical Session quarantined, and MUST NOT represent the turn as safely stopped. The runtime MUST reject later work on that physical Session with `unavailable-runtime` until stop is observed or the Runner process restarts; different physical Sessions SHALL remain available. Runner restart clears the quarantine because process termination ends every in-process Pi turn. The runtime MUST NOT automatically replay the prompt after any timeout, interruption, or uncertain submission result.

#### Scenario: Deadline fixes timeout before interruption cleanup

- **WHEN** the turn deadline arrives before the SDK prompt operation resolves
- **THEN** the runtime SHALL fix the outcome as deadline exceeded before requesting interruption
- **AND** the Workflow Action SHALL report `timeout` even if the prompt resolves later

#### Scenario: Workflow executor declares the fixed deadline

- **WHEN** the Workflow executor constructs a `mohist/pi` turn
- **THEN** it SHALL declare a deadline exactly 60 minutes after turn start
- **AND** the runtime SHALL inject exactly one task-independent wrap-up warning at 55 minutes and interrupt at 60 minutes

#### Scenario: Action input cannot override the deadline

- **WHEN** a `mohist/pi` task supplies a top-level or options key named `timeout` or `deadline`
- **THEN** normal Action input validation SHALL reject or diagnose that unknown key according to its location
- **AND** the Workflow turn deadline SHALL remain 60 minutes

#### Scenario: Uncertain submission is not replayed

- **WHEN** the Runner cannot determine whether a prompt was admitted or completed
- **THEN** the runtime MUST NOT automatically submit that prompt again
- **AND** it SHALL report the uncertain interruption or failure to the work owner

#### Scenario: Unconfirmed interruption quarantines the physical Session

- **WHEN** Pi interruption is requested but the runtime cannot confirm that streaming stopped
- **THEN** the current turn SHALL report interruption as unconfirmed
- **AND** later work targeting that physical Session SHALL fail with `unavailable-runtime` instead of starting another Prompt
- **AND** work on different physical Sessions SHALL remain admissible

#### Scenario: Confirmed stop or Runner restart clears quarantine

- **WHEN** the runtime later observes that the quarantined Pi turn stopped, or the Runner process restarts
- **THEN** the physical Session SHALL become eligible for a later turn
- **AND** the failed Prompt MUST NOT be replayed

#### Scenario: External cancellation fixes interruption before cleanup

- **WHEN** the Action's external cancellation signal arrives before the SDK prompt operation resolves
- **THEN** the runtime SHALL fix the outcome as `interrupted` before requesting Pi interruption
- **AND** a late prompt resolution MUST NOT replace the interrupted result
- **AND** the Prompt MUST NOT be replayed

#### Scenario: Unconfirmed cancellation quarantines the physical Session

- **WHEN** cancellation requests Pi interruption but the runtime cannot confirm that streaming stopped
- **THEN** the current turn SHALL remain `interrupted` with an unconfirmed-stop diagnostic
- **AND** later work targeting that physical Session SHALL fail with `unavailable-runtime` until stop is observed or the Runner process restarts

### Requirement: Non-recoverable provider failures end the turn promptly

The runtime SHALL derive provider retry facts from Pi retry events and MUST NOT scan log text. A retry error that indicates exhausted quota, credit, balance, billing, plan allowance, or usage limit SHALL be non-recoverable on its first occurrence. A recoverable error whose retry attempt reaches the Runner-configured threshold, defaulting to five, SHALL also become non-recoverable. Ordinary transient rate limits below the threshold SHALL remain available for Pi to retry. On a non-recoverable judgment, the runtime SHALL interrupt the turn, verify interruption, return `turn-failed` with the original provider message as diagnostics, preserve the Session binding, and MUST NOT wait for further retries.

#### Scenario: Exhausted quota fails on first retry event

- **WHEN** a Pi retry event reports exhausted quota, credit, balance, billing, plan allowance, or usage limit
- **THEN** the runtime SHALL interrupt and fail the turn on that first event
- **AND** it MUST NOT wait for Pi to perform another retry

#### Scenario: Transient failure remains retryable below the threshold

- **WHEN** Pi reports a recoverable provider failure with an attempt below the configured threshold
- **THEN** the runtime SHALL leave retry control to Pi
- **AND** it MUST NOT fail the turn solely because that transient event occurred

#### Scenario: Retry threshold ends a repeated failure

- **WHEN** a recoverable provider error reaches the configured retry-attempt threshold before the turn completes
- **THEN** the runtime SHALL interrupt and fail the turn as `turn-failed`
- **AND** it SHALL preserve the current physical Session binding

#### Scenario: Unconfirmed provider abort preserves failure and quarantines

- **WHEN** a non-recoverable provider failure requests interruption but Pi stop cannot be confirmed
- **THEN** the turn SHALL remain `turn-failed` with the sanitized provider message and an interruption-unconfirmed diagnostic
- **AND** the physical Session and logical Session key SHALL be quarantined before return until stop is observed or the Runner process restarts

### Requirement: Provider failure policy has validated Runner configuration

The Runner SHALL configure the shared provider failure policy at startup. `MOHIST_PROVIDER_RETRY_THRESHOLD` SHALL accept a positive integer and default to `5`. `MOHIST_PROVIDER_NON_RECOVERABLE_TERMS` SHALL accept a JSON array of non-empty literal strings that are matched case-insensitively in addition to the built-in quota, credit, balance, billing, plan allowance, and usage-limit terms. Invalid values SHALL prevent Pi readiness and SHALL produce an actionable configuration diagnostic; regular expressions supplied by operators MUST NOT be evaluated. The same parsed policy object SHALL be supplied to OpenCode and Pi so the promised failure semantics have one configuration authority.

#### Scenario: Non-default retry threshold reaches Pi

- **WHEN** the Runner starts with `MOHIST_PROVIDER_RETRY_THRESHOLD=3`
- **THEN** Pi SHALL classify the third consecutive recoverable provider retry as non-recoverable

#### Scenario: Additional literal term fails immediately

- **WHEN** `MOHIST_PROVIDER_NON_RECOVERABLE_TERMS` contains `monthly allowance exhausted` and a provider retry message contains that text with different letter case
- **THEN** the runtime SHALL classify the first occurrence as non-recoverable

#### Scenario: Invalid policy configuration blocks readiness

- **WHEN** the retry threshold is not a positive integer or the additional terms value is not a JSON array of non-empty strings
- **THEN** Pi SHALL remain not-ready with an actionable configuration diagnostic
- **AND** no operator-supplied regular expression SHALL execute

### Requirement: Default tests isolate the Pi SDK and external environment

Default tests of Pi behavior SHALL use a fake Pi runtime or fake SDK factory. They MUST NOT use a real provider, network, process, physical filesystem configuration, or wall clock, and SHALL drive Session events, completion, interruption, and failure deterministically.

#### Scenario: Runtime tests use deterministic fakes

- **WHEN** the default test suite exercises Pi startup, turn execution, events, deadlines, or provider failures
- **THEN** it SHALL inject a fake runtime or SDK factory
- **AND** it MUST NOT contact real Pi providers or depend on the host environment
