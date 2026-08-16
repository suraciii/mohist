### Requirement: Resource outcomes have distinct machine-readable classifications
The Runner SHALL distinguish a genuine per-work enforcement overrun from an invalid budget declaration, unavailable capacity/configuration, insufficient capacity for a valid budget, and an ordinary command failure. A genuine enforcement overrun SHALL retain the `resource-containment` classification. Budget-configuration outcomes MUST use a classification distinct from `resource-containment` and `script-failed`, and an ordinary non-zero command exit without an observed containment event SHALL remain a command or test failure.

#### Scenario: The process tree breaches its effective bound
- **WHEN** the resource enforcer observes that a work's process tree exceeded its effective memory or execution-duration bound and terminates that tree
- **THEN** the normal work result carries the `resource-containment` classification
- **AND** the result is not presented as an indistinguishable test or script failure

#### Scenario: A valid budget cannot be admitted
- **WHEN** a valid verification declaration cannot fit the available host capacity or required concurrency reservation
- **THEN** the normal work result carries an insufficient-budget classification distinct from `resource-containment`
- **AND** the result states that execution was not admitted because the budget could not be satisfied

#### Scenario: The budget declaration or capacity source is invalid or unavailable
- **WHEN** the Runner cannot validate the budget declaration or cannot obtain a required host-capacity/configuration input
- **THEN** the normal work result carries an invalid-budget or unavailable-budget classification distinct from an execution overrun
- **AND** the verification command is not reported as having exceeded its process limit

#### Scenario: The command exits with a test failure
- **WHEN** a verification or ordinary command exits non-zero without an observed per-work containment event
- **THEN** the result remains the normal command or test failure classification
- **AND** the captured stdout and stderr failure evidence remains available through the existing work-result and task-log paths

### Requirement: Containment diagnostics identify the effective work and budget context
Every resource-related failure reported through the normal work-result path SHALL expose the effective workflow task or work identity, outcome classification, affected resource dimension, effective bound, budget source, and an actionable reason. When a host-capacity value or requested-versus-effective comparison is available, the diagnostic SHALL expose that context as well. The diagnostic MUST be available without inferring it from an opaque shell exit code.

#### Scenario: An actual memory overrun is reported
- **WHEN** a full verification process tree exceeds its effective memory limit
- **THEN** the failed work result identifies the verification task and work identity
- **AND** it identifies memory as the affected dimension, the effective memory bound, the budget source, and the action needed to change or partition the workload

#### Scenario: A budget configuration is rejected before execution
- **WHEN** a verification budget is rejected because a declaration, host input, or capacity reservation is invalid or unavailable
- **THEN** the failed work result identifies the verification task and the rejected budget context
- **AND** it names the missing or invalid input and the corrective configuration or scheduling action
- **AND** it does not claim that the command produced a test failure

#### Scenario: A normal test failure includes diagnostic output
- **WHEN** the current full verification command exits because a test or check fails while remaining inside its effective resource bound
- **THEN** the result identifies the failed command outcome rather than resource containment
- **AND** stdout and stderr remain available with the existing bounded-output behavior so the test failure can be diagnosed

### Requirement: The resource enforcer reports only observed enforcement breaches
The Runner SHALL mark a result as `resource-containment` only when the per-work enforcement mechanism observes or authoritatively records that the effective bound caused termination. A signal, non-zero exit code, host error, or resource-related text without evidence that this work breached its effective bound MUST NOT by itself be classified as `resource-containment`.

#### Scenario: A process exits abnormally without a containment observation
- **WHEN** a fake or real process exits with a signal or resource-like error but no watchdog, kernel limit, or authoritative containment event identifies a bound breach
- **THEN** the Runner reports the corresponding ordinary process failure or an unavailable-host diagnostic
- **AND** it does not label the result as a per-work containment overrun

#### Scenario: A watchdog observes the breach before process close
- **WHEN** the process-tree watchdog observes the effective bound and initiates process-group termination before the child closes
- **THEN** the command result records containment independently of the final exit code
- **AND** the action maps that fact to the resource-overrun classification with the captured output preserved

#### Scenario: A declared verification task has no shorter Action deadline
- **WHEN** a built-in verification task is dispatched with a valid `resourceBudget.wallClockMs`
- **THEN** process-tree enforcement uses that resolved wall-clock bound as the task deadline
- **AND** the built-in task does not supply the legacy `with.timeout: 300000` deadline
- **AND** an ordinary `with.timeout` value cannot preempt the declared verification budget

### Requirement: Resource-related failures settle through the normal result protocol
A resource overrun or budget-configuration failure SHALL produce a definite failed `WorkItemResult` with its error classification and diagnostic context, and SHALL be durably reportable under the original work identity. The Runner MUST NOT leave the work pending, throw an opaque exception that bypasses settlement, or convert the outcome into an unknown result solely because containment or budget validation stopped execution.

#### Scenario: Containment terminates a workflow script
- **WHEN** `core/script` is terminated by per-work containment
- **THEN** the Runner reports a definite failed result for the same task and work identity
- **AND** the report includes the resource classification, effective budget context, and exit information available from the command
- **AND** report retries continue under the existing at-least-once acknowledgement contract until the owner durably acknowledges or marks the report stale

#### Scenario: Budget validation prevents a command from starting
- **WHEN** budget validation fails before `core/script` starts its shell
- **THEN** the Runner reports a definite failed result through the same work-report path
- **AND** the result identifies the configuration or admission problem rather than fabricating command output

### Requirement: Genuine containment overruns remain terminal for automatic recovery
A work result classified as `resource-containment` SHALL remain excluded from automatic repair-task and self-retry recovery. The Runner SHALL preserve this terminal diagnostic boundary so a repeated invocation cannot loop against the same fixed resource constraint. Budget-configuration outcomes SHALL remain distinguishable from this terminal overrun rule.

#### Scenario: A contained verification work has a recovery declaration
- **WHEN** a verification task reports `resource-containment` and declares automatic recovery or self-retry
- **THEN** the Runner does not schedule that recovery solely for the containment result
- **AND** the owner receives the failed diagnostic result for operator or configuration action

#### Scenario: A budget configuration problem is reported
- **WHEN** a verification task cannot start because its budget is invalid, unavailable, or insufficient
- **THEN** the result uses the budget-configuration classification and does not claim that a running process breached its bound
- **AND** any recovery decision is made against that distinct configuration classification rather than being silently treated as an actual overrun

### Requirement: Per-work containment preserves sibling isolation and Runner liveness
Containment SHALL apply to the complete process tree belonging to the affected work only. Terminating one work's process group MUST NOT terminate, corrupt, or strand sibling work, the Runner process, or results already awaiting acknowledgement. After the termination, the Runner SHALL release the affected execution capacity and remain able to execute and report later work.

#### Scenario: A runaway work is contained beside a healthy sibling
- **WHEN** one in-flight work exceeds its per-work bound while a sibling work executes on the same Runner
- **THEN** only the runaway work's process tree is terminated
- **AND** the sibling continues and reports its own result normally
- **AND** the Runner remains alive and operational

#### Scenario: New work arrives after containment
- **WHEN** the Runner receives new work after a prior work was terminated by containment
- **THEN** the new work can execute under its own resource limits and report normally
- **AND** the prior containment result remains associated with its original work identity

#### Scenario: A completed sibling result is awaiting acknowledgement
- **WHEN** containment occurs while another work's completed result is retained for report acknowledgement
- **THEN** that result remains retryable under its original identity
- **AND** containment does not discard or rewrite the sibling result

### Requirement: Resource classification is covered by deterministic and focused evidence
The implementation SHALL include deterministic fake resource and process coverage for actual memory or duration breaches, false-positive exit classification, invalid or unavailable budget inputs, insufficient capacity, normal result settlement, and sibling isolation. Focused evidence SHALL exercise the current complete verification command far enough to show its resolved budget and preserve meaningful command/test output. Evidence collection MUST NOT retry, replay, or mutate the currently blocked workflow runs.

#### Scenario: Fake containment and configuration tests run
- **WHEN** the fake resource/process suite simulates each resource outcome and result-settlement path
- **THEN** the assertions distinguish actual containment from budget/configuration failure and ordinary command failure
- **AND** the assertions prove that a contained work cannot kill its sibling or Runner

#### Scenario: Current full verification evidence is collected
- **WHEN** the current `ci.verify` command is run as focused change evidence under the new contract
- **THEN** the evidence records the effective task identity and budget context and retains the command's test output
- **AND** no blocked workflow run is retried or otherwise changed to obtain the evidence
