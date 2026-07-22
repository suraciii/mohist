### Requirement: Single authoritative parse entry and shared semantic model

Workflow Definition YAML MUST be validated through one authoritative `Parse(yaml) → Definition | Error[]` entry point that yields the single semantic model and error type. The Profile save path, the local validation command, and CI MUST all consume that one entry point rather than each re-parsing an approximate structure. The model, parser, and validator MUST live in a standalone library (`Mohist.Workflow.Definition`) with no Orleans and no ASP.NET dependency, so server and CLI share the same code.

#### Scenario: one entry point serves every consumer
- **WHEN** a Definition is parsed by the Profile save path, the `mo workflow validate` command, and the CI golden-case check
- **THEN** each consumer obtains the same `Definition` model and the same error type from the same `Parse` entry point

#### Scenario: standalone library carries no host dependency
- **WHEN** the validator library is referenced by the CLI
- **THEN** it introduces no Orleans and no ASP.NET dependency

### Requirement: Full single-pass error collection

The validator MUST collect every detectable problem in one pass instead of interrupting on the first error. Every error MUST carry a YAML path that locates the offending node and a message written in domain language. Errors MUST NOT expose exception stack traces or implementation terms.

#### Scenario: multiple problems returned together
- **WHEN** a Definition contains an unknown field in one task and a type error in a later task
- **THEN** the validator reports both errors in a single result rather than stopping at the first

#### Scenario: error location uses YAML path
- **WHEN** the second recovery handler of the first task in the second stage is malformed
- **THEN** the error path identifies `stages[1].tasks[0].recovery.handlers[1]` and the message names the domain problem

### Requirement: Unknown fields are rejected

An unknown key at any level of the Definition MUST be reported as an error. The validator MUST NOT silently drop or ignore unknown fields.

#### Scenario: misspelled field is an error
- **WHEN** a recovery handler declares `retryself` instead of `retrySelf`
- **THEN** the validator reports an unknown-field error pointing at the misspelled key

#### Scenario: nested unknown field is reported
- **WHEN** a task declares an unrecognized property such as `dependson`
- **THEN** the validator reports an unknown-field error at that task's path

### Requirement: Type errors are reported without silent defaults

A field whose YAML value does not match its required type MUST be reported as an error. The validator MUST NOT coerce an invalid value into a default.

#### Scenario: budget is not a non-negative integer
- **WHEN** a recovery declares `budget: abc`
- **THEN** the validator reports a type error and does not substitute a default budget

#### Scenario: boolean field receives a non-boolean
- **WHEN** a stage declares `requiresApproval: "yes"`
- **THEN** the validator reports a type error

### Requirement: Top-level structure is closed

The Definition top level MUST accept only `approval` and `stages`. `stages` MUST be non-empty. Profile metadata, `variables`, `defaults`, and top-level `artifacts` MUST be rejected as unknown fields and MUST NOT enter the semantic model or be silently ignored.

#### Scenario: only approval and stages are accepted
- **WHEN** a Definition declares a top-level `variables` block
- **THEN** the validator reports an unknown-field error and the block does not appear in the parsed model

#### Scenario: empty stages rejected
- **WHEN** a Definition declares `stages: []`
- **THEN** the validator reports that stages must be non-empty

### Requirement: Identifiers are unique and required fields are enforced

Each stage `name`, task `id`, and check `id` MUST be non-empty and unique within its scope: stage names unique across the Definition, task ids unique within their task list, and check ids unique within their stage. A task and a check MUST declare a `uses` action. The Definition MUST NOT require `title`.

#### Scenario: duplicate task id rejected
- **WHEN** two tasks in the same task list share the id `build`
- **THEN** the validator reports a duplicate-identifier error at the second task

#### Scenario: missing uses rejected
- **WHEN** a task or check omits `uses`
- **THEN** the validator reports that `uses` is required

#### Scenario: title may be omitted
- **WHEN** a task omits `title`
- **THEN** the validator accepts the task

### Requirement: Checks identify by id

A check MUST be identified by `id`. The Definition MUST NOT accept `name` as a check identifier.

#### Scenario: check uses id
- **WHEN** a check declares `id: lint`
- **THEN** the validator accepts the check and recognizes `lint` as its stage-internal identifier

#### Scenario: check name rejected
- **WHEN** a check declares `name: lint` instead of `id`
- **THEN** the validator reports an unknown-field error for `name` and a missing-required error for `id`

### Requirement: Stage resource locking is constrained

`lockBehavior` MUST be either absent or the value `sequential`, and it MUST appear only alongside a non-empty `resources` list. `resources` MUST NOT appear without `lockBehavior`.

#### Scenario: lockBehavior without resources rejected
- **WHEN** a stage declares `lockBehavior: sequential` with no `resources`
- **THEN** the validator reports that lockBehavior requires non-empty resources

#### Scenario: resources without lockBehavior rejected
- **WHEN** a stage declares `resources` without `lockBehavior`
- **THEN** the validator reports that resources require lockBehavior

#### Scenario: non-sequential lockBehavior rejected
- **WHEN** a stage declares `lockBehavior: parallel`
- **THEN** the validator reports that lockBehavior must be sequential

### Requirement: Recovery is well-formed

Recovery `budget` MUST be a non-negative integer. Recovery `handlers` MUST be non-empty and ordered. At most one handler MAY omit `when`; that default handler MUST be the last handler. A handler MUST declare `tasks` or `retrySelf` (or both). A `when` clause MUST take the form `field=value` with both sides non-empty.

#### Scenario: budget negative rejected
- **WHEN** a recovery declares `budget: -1`
- **THEN** the validator reports that budget must be a non-negative integer

#### Scenario: two default handlers rejected
- **WHEN** two recovery handlers omit `when`
- **THEN** the validator reports that at most one default handler is allowed

#### Scenario: default handler not last rejected
- **WHEN** a handler omitting `when` precedes a handler that declares `when`
- **THEN** the validator reports that the default handler must be last

#### Scenario: handler without tasks or retrySelf rejected
- **WHEN** a recovery handler declares neither `tasks` nor `retrySelf`
- **THEN** the validator reports that a handler must declare tasks or retrySelf

### Requirement: Auxiliary declarations are valid

`setVars` keys MUST be non-empty and each value MUST be an `output.`-prefixed field path. `expect.files[].path` MUST be non-empty, `expect.markers[].oneOf` MUST be non-empty, and `expect.markers[].failIf` MUST be a member of `oneOf`. `artifacts.files[].path` MUST be non-empty.

#### Scenario: setVars value without output prefix rejected
- **WHEN** a task declares `setVars: { result: status }`
- **THEN** the validator reports that the setVars value must be an output field path

#### Scenario: marker failIf outside oneOf rejected
- **WHEN** a marker declares `failIf: x` while `oneOf` does not contain `x`
- **THEN** the validator reports that failIf must be a member of oneOf

### Requirement: with is an open structure

A task or check `with` MUST be either absent or a JSON object. The validator MUST recursively validate `${{ }}` template expressions inside `with` values. The validator MUST NOT treat unknown `with` keys as errors and MUST NOT interpret their required or value types, which belong to the Action catalog.

#### Scenario: with unknown key is not an error
- **WHEN** a task declares `with: { anything: 1 }`
- **THEN** the validator reports no error for the key `anything`

#### Scenario: with must be an object
- **WHEN** a task declares `with: "plain string"`
- **THEN** the validator reports that with must be a JSON object

#### Scenario: template expression inside with is validated
- **WHEN** a `with` value contains `${{ tasks.missing.outputs.x }}` referencing an undeclared task
- **THEN** the validator reports the template-reference error even though the `with` key is unknown

### Requirement: Template references use public roots and declared tasks

Every `${{ }}` expression MUST be syntactically resolvable and its root MUST belong to the public root table (`workflow`, `stage`, `work`, `issue`, `repository`, `workspace`, `vars`, `tasks`, `prompts`, `failure`). A `tasks.<id>` reference MUST resolve to a task declared in the Definition that can execute before the reference position; a reference to the enclosing task's own id or to a task that can only execute later MUST be rejected at validation.

#### Scenario: off-table root rejected
- **WHEN** an expression uses `${{ project.id }}`
- **THEN** the validator reports that the root is not in the public table

#### Scenario: reference to undeclared task rejected
- **WHEN** an expression uses `${{ tasks.ghost.outputs.x }}` and no task with id `ghost` is declared
- **THEN** the validator reports that `tasks.ghost` does not reference a declared task

#### Scenario: self reference rejected
- **WHEN** a task whose id is `build` references `${{ tasks.build.outputs.x }}` in its own inputs
- **THEN** the validator rejects the self reference at validation

#### Scenario: forward reference rejected
- **WHEN** a task references `${{ tasks.deploy.outputs.x }}` and `deploy` is declared later in execution order
- **THEN** the validator rejects the forward reference at validation

### Requirement: Template position rules are enforced

`failure.*` MUST appear only inside recovery-handler tasks. `work.approvalFeedback.*` MUST appear only inside approval-feedback tasks. A reference in any other position MUST be rejected. Static validation MUST NOT claim that a referenced task will succeed or that a referenced output field will exist at runtime.

#### Scenario: failure outside recovery rejected
- **WHEN** a stage task that is not a recovery handler references `${{ failure.error }}`
- **THEN** the validator reports that failure.* is allowed only inside recovery-handler tasks

#### Scenario: approvalFeedback outside feedback task rejected
- **WHEN** a task that is not an approval-feedback task references `${{ work.approvalFeedback.summary }}`
- **THEN** the validator reports that work.approvalFeedback.* is allowed only inside approval-feedback tasks

#### Scenario: runtime output absence is not a validation concern
- **WHEN** a valid forward-safe `tasks.<id>.outputs.x` reference is validated
- **THEN** the validator accepts the reference without asserting the task will succeed or that `x` will be produced at runtime

### Requirement: Profile save entry rejects invalid Definitions and separates error sources

The Profile save entry MUST run the authoritative validator and reject an invalid Definition with the full error list. Definition-language errors and Action-contract errors MUST share one YAML-path rule and MUST be distinguishable by source. No validation rule MUST be duplicated between the Definition validator and the Action catalog; each rule MUST have exactly one owner.

#### Scenario: invalid Definition rejected with full list
- **WHEN** a Profile save carries a Definition with several errors
- **THEN** the save is rejected and the response carries the complete Definition error list, not only the first error

#### Scenario: error sources are distinguishable
- **WHEN** a Definition passes the validator but a task's `uses` is not in the Action catalog
- **THEN** the resulting error is reported as an Action-contract error with the same YAML-path rule, distinct in source from a Definition-language error
