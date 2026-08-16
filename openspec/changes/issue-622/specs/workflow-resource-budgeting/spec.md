### Requirement: Verification tasks declare an auditable resource budget
A full verification task that runs the complete `ci.verify` workload SHALL declare either one finite task budget for its complete process tree or a finite, validated set of verification partitions. The declaration MUST identify the workflow task or partition, the bounded resource dimensions, the declared values, and the source from which each value is derived. The Runner MUST NOT silently apply an opaque named override as the only budget contract for full verification.

#### Scenario: The complete verification task resolves from an explicit declaration
- **WHEN** the build-stage verification task is dispatched with the complete `ci.verify` command
- **THEN** the Runner resolves a finite budget for the shell and every descendant process in that task
- **AND** the resolved result identifies the effective task, bounded dimensions, declared values, and budget source

#### Scenario: A partitioned verification workload is declared
- **WHEN** the workflow represents verification as multiple sequential or explicitly scheduled partitions
- **THEN** every partition has its own finite declaration and identity
- **AND** the workflow definition identifies the order or concurrency relationship needed to validate the aggregate budget
- **AND** no partition executes under an undeclared ordinary-work fallback

### Requirement: Effective verification budgets are finite and capacity-aware
The Runner SHALL resolve an effective verification budget from the workflow declaration, valid Runner deployment configuration, and the enforceable host or Runner capacity. The effective memory and wall-clock limits SHALL be finite positive bounds, and the aggregate capacity of concurrently admitted verification work SHALL remain within the enforceable host limit. A valid declaration that cannot fit the available capacity SHALL fail admission or use a validated partitioning plan; it MUST NOT be silently reduced below the validated workload requirement or changed to an unbounded execution.

#### Scenario: A declared budget fits the available host capacity
- **WHEN** the host capacity and current concurrency can accommodate the declared verification budget
- **THEN** the Runner admits the work with the resolved finite budget
- **AND** the effective budget remains subject to the Runner's aggregate host protection

#### Scenario: Concurrent verification budgets would exceed host capacity
- **WHEN** admitting another verification task would make the aggregate effective budgets exceed the host or Runner capacity
- **THEN** the scheduler defers or rejects that admission, or selects a previously validated partition plan that fits
- **AND** it MUST NOT start the task with an unvalidated budget
- **AND** it MUST NOT disable the host-level or per-work containment boundary

#### Scenario: Host capacity information is unavailable
- **WHEN** the Runner cannot obtain the capacity information required to validate the declared verification budget
- **THEN** the verification task is rejected with a budget-configuration outcome before its command starts
- **AND** the failure identifies the unavailable capacity input and the configuration action required to continue

### Requirement: Full verification remains bounded without changing ordinary-work policy
The complete verification command SHALL execute under a finite process-tree memory and execution-duration budget, whether it is represented as one task or validated partitions. The budget SHALL apply to the shell and all descendants, and SHALL preserve the configured watchdog and wall-clock protections. Ordinary work SHALL retain the existing default containment policy when no override is supplied, including the conservative per-work defaults.

#### Scenario: Full verification stays within its effective budget
- **WHEN** the `npm ci`, server test, Web check, and Runner check phases complete within the resolved limits
- **THEN** the verification task completes with the command's normal structured result and captured test output
- **AND** no ordinary work item's resource limit is changed as a side effect

#### Scenario: Full verification exceeds its finite process-tree budget
- **WHEN** the verification shell or any descendant exceeds the effective memory or execution-duration limit
- **THEN** only that verification work is terminated through the containment mechanism
- **AND** the work produces a resource-overrun result with the effective budget context
- **AND** the Runner and unrelated work remain operational

#### Scenario: An ordinary command has no verification declaration
- **WHEN** an ordinary command is dispatched without a verification budget declaration
- **THEN** it uses the configured ordinary-work resource limits or their existing defaults
- **AND** it does not inherit the full-verification budget

### Requirement: Budget and workflow configuration is validated before execution
Workflow validation SHALL reject a verification declaration that is missing a required bounded dimension, uses a non-finite or non-positive value, names an unsupported budget source or partition, or cannot be reconciled with the host-capacity contract. Validation SHALL report the workflow path, task or partition identity, invalid field, and corrective reason. A rejected declaration MUST NOT invoke its verification command.

#### Scenario: A profile contains an invalid verification budget
- **WHEN** server validation loads a workflow profile whose verification budget is malformed, missing, or unsupported
- **THEN** validation rejects the profile with a path-specific budget error
- **AND** the error names the invalid task or partition and the required correction
- **AND** no Runner is asked to execute that verification task

#### Scenario: A valid budget becomes insufficient at admission time
- **WHEN** a previously valid verification declaration cannot fit the Runner's current capacity or concurrency state
- **THEN** admission reports an insufficient-budget outcome tied to the effective task identity
- **AND** it does not misreport the condition as a test failure or as a process that already exceeded its bound

### Requirement: Built-in verification profiles have equivalent budget semantics
The `mohist/local` and `mohist/github-pr` built-in profiles SHALL expose the same full-verification budget contract, resolution rules, capacity checks, and validation requirements for their build-stage verification task. Profile-specific agent actions or surrounding build tasks MUST NOT change the effective verification budget semantics.

#### Scenario: Both built-in profiles are validated
- **WHEN** the server validates the `mohist/local` and `mohist/github-pr` workflow definitions
- **THEN** both build-stage verification tasks pass the same budget-contract validation
- **AND** both resolve the same budget dimensions from the same declared source and host-capacity rules

#### Scenario: One built-in profile drifts from the contract
- **WHEN** either built-in profile omits the declaration, uses a different unsupported source, or supplies a different resolution rule for full verification
- **THEN** profile validation fails and identifies the drifting profile and verification task
- **AND** the profile is not treated as equivalent merely because its command input is `vars.ci.verify`

### Requirement: Budget selection and verification evidence are operationally auditable
Runner documentation and deployment configuration guidance SHALL explain how the effective full-verification budget is selected, how `WORK_RESOURCE_*` settings interact with the workflow declaration and host capacity, and how each budget outcome is diagnosed. The implementation SHALL provide deterministic fake resource and process evidence for resolution, capacity rejection, bounded execution, and the current complete verification command. Evidence collection MUST NOT retry or mutate the currently blocked workflow runs.

#### Scenario: An operator audits a verification budget
- **WHEN** an operator inspects the workflow task, Runner configuration, and Runner result for a full verification attempt
- **THEN** the operator can determine the declared source, host-capacity inputs, effective bounds, and admission decision
- **AND** the documentation identifies the configuration change needed for an invalid, unavailable, or insufficient budget

#### Scenario: Focused verification evidence is collected
- **WHEN** the change is validated with fake resource/process tests and the current complete `ci.verify` command
- **THEN** the evidence covers both successful bounded execution and the relevant failure classifications
- **AND** no existing blocked workflow run is retried, replayed, or otherwise mutated as part of that evidence
