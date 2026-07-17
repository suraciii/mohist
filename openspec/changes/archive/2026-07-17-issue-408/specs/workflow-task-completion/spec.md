### Requirement: Workflow owns post-Action completion evaluation

The Workflow task executor SHALL evaluate task-level `expect` after the selected Action returns. Actions and execution runtimes MUST NOT receive or interpret the task-level completion contract, send an implicit repair turn, or change session status based on completion-policy evaluation. A successful Action SHALL produce an ordinary failed completion result when its completion contract is not satisfied. An Action failure SHALL remain an ordinary task failure. In either case, declared recovery SHALL replace the ordinary result only when a handler matches the final Action output and has remaining budget; otherwise the ordinary result SHALL remain.

#### Scenario: An unmet expectation does not trigger an implicit repair turn

- **WHEN** an Action succeeds but its task-level completion contract is not satisfied
- **THEN** the task executor SHALL produce a failed completion result
- **AND** the Action SHALL have been invoked exactly once for that attempt

#### Scenario: Expectations do not rescue an Action failure

- **WHEN** an Action fails and no recovery handler matches its output
- **THEN** the task SHALL fail even if files or markers happen to satisfy `expect`

### Requirement: Completion contracts are expanded separately for each dispatch

Workflow SHALL expand supported template expressions in task-level `expect` for each dispatch using that dispatch's effective Variables and runtime context. Whole-value expressions SHALL preserve their JSON type. The expanded completion contract SHALL remain separate from the independently expanded Action Input, and an already dispatched attempt SHALL retain its expanded contract while a later task or retry uses the values effective for its own dispatch.

#### Scenario: An expected path uses dispatch context

- **WHEN** a top-level marker path contains `${{ openspecChangeDir }}` and that value resolves for the dispatch
- **THEN** completion evaluation SHALL use the resolved path
- **AND** the resolved `expect` MUST NOT appear in Action Input

#### Scenario: A retry expands against current Variables

- **WHEN** a variable referenced by top-level `expect` changes after one attempt is dispatched and before a retry is dispatched
- **THEN** the first attempt SHALL retain its original expanded completion contract
- **AND** the retry SHALL use the variable value effective for the retry dispatch

### Requirement: Required files and file markers are conjunctive completion conditions

Every path in `expect.files` SHALL exist when completion is evaluated. Every file-backed entry in `expect.markers` SHALL target an existing file and SHALL match at least one configured `oneOf` value, or the configured `contains` value when that marker form is used. If file content contains multiple configured `oneOf` values, the matched value SHALL be the first present value in declaration order. All declared files and markers SHALL be satisfied; one satisfied entry MUST NOT mask another missing entry. Relative paths SHALL resolve within the task working directory.

#### Scenario: All required files and markers are present

- **WHEN** the Action succeeds, every declared file exists, and every marker target contains an accepted marker
- **THEN** file and marker evaluation SHALL be satisfied
- **AND** the task SHALL remain eligible to complete

#### Scenario: A required file is missing

- **WHEN** the Action succeeds but a path declared in `expect.files` does not exist
- **THEN** the task SHALL fail completion
- **AND** the failure detail SHALL identify the missing path

#### Scenario: No accepted marker is present

- **WHEN** a marker target exists but contains none of its configured accepted values
- **THEN** the task SHALL fail completion
- **AND** the failure detail SHALL identify the target and accepted values

#### Scenario: A file contains both passing and failing markers

- **WHEN** a file-backed marker declares `oneOf` as `PASS`, then `FAIL`, and the file contains both values
- **THEN** the matched value SHALL be `PASS` because it is the first present value in declaration order
- **AND** a `failIf` value of `FAIL` MUST NOT trigger for that match

#### Scenario: Artifact capture alone does not require a file

- **WHEN** a path is declared only under `artifacts.files` and is absent after the Action
- **THEN** best-effort artifact capture SHALL skip that path without failing completion
- **AND** the same absent path SHALL fail completion when it is also declared under `expect.files`

### Requirement: `failIf` converts an accepted marker into failure

A marker that matches an accepted value SHALL satisfy the marker-presence condition. If the matched value also equals that marker's `failIf`, the task SHALL fail completion while retaining the matched value for Action-specific output projection and recovery matching. A different accepted value MUST NOT trigger `failIf`.

#### Scenario: A non-failing accepted marker passes

- **WHEN** a marker accepts `PASS` and `FAIL`, declares `failIf` as `FAIL`, and matches `PASS`
- **THEN** the marker SHALL satisfy completion
- **AND** `failIf` MUST NOT fail the task

#### Scenario: A failing accepted marker remains available to recovery

- **WHEN** the same marker matches `FAIL`
- **THEN** the task SHALL have a failed completion result
- **AND** the matched `FAIL` value SHALL remain available for the selected Action's output contract and recovery evaluation

### Requirement: `_output` markers evaluate the final assistant text fact

The reserved marker path `_output` SHALL evaluate the final assistant text from the completed turn instead of reading a file. The text SHALL arrive as a private Action-result fact and MUST NOT be copied into Action Output, TaskRun output, captured output, Variables, or artifacts. `_output` SHALL use the marker's configured accepted values and `failIf` semantics. If more than one configured accepted value occurs in the text, the matched value SHALL be the accepted occurrence that appears last. If no final assistant text fact is available, the `_output` marker SHALL be unsatisfied.

#### Scenario: Final assistant text contains an accepted marker

- **WHEN** an Action result carries final assistant text containing a value accepted by an `_output` marker
- **THEN** `_output` marker evaluation SHALL use that text and SHALL be satisfied
- **AND** no filesystem path named `_output` SHALL be read

#### Scenario: Final assistant text contains no accepted marker

- **WHEN** final assistant text is present but contains none of the configured accepted values
- **THEN** the task SHALL fail completion with `_output` identified as unsatisfied

#### Scenario: Final assistant text contains successive promise markers

- **WHEN** an `_output` marker accepts `<promise>PASS</promise>` and `<promise>FAIL</promise>`, and the final assistant text contains PASS followed by FAIL
- **THEN** the matched marker SHALL be `<promise>FAIL</promise>`
- **AND** a matching `failIf` SHALL fail the ordinary completion result and expose `promise=FAIL` to recovery

#### Scenario: The turn fact is unavailable

- **WHEN** a task declares an `_output` marker but the Action result carries no final assistant text fact
- **THEN** the task SHALL fail completion
- **AND** the executor MUST NOT obtain the text from Action Output as a fallback

#### Scenario: Final assistant text remains private

- **WHEN** `_output` evaluation succeeds or fails
- **THEN** downstream task-output expressions SHALL NOT expose the full assistant text
- **AND** persisted Action Output MUST NOT contain the text solely because completion evaluated it

### Requirement: Action-owned output remains authoritative

Workflow completion SHALL preserve the output defined by the selected Action and MUST NOT impose a platform-wide business output schema. Completion diagnostics and private turn facts SHALL remain outside Action Output. An Action-specific output projection SHALL occur only where that Action contract explicitly defines it.

#### Scenario: A non-agent Action output is preserved

- **WHEN** an Action returns output containing fields such as `errorCode`, `prNumber`, or another Action-owned value
- **THEN** completion evaluation SHALL preserve those fields unchanged
- **AND** it MUST NOT replace them with expectation diagnostics or a generic promise object

### Requirement: Recovery evaluates the final task output after completion policy

Recovery matching SHALL evaluate the final Action-owned output, including any Action-specific projection derived from a matched completion marker, before the task outcome is finalized. Matching SHALL remain independent of whether the normalized result is completed or failed. If a handler matches and has remaining budget, its declared follow-up behavior SHALL run; otherwise the ordinary completion result SHALL remain.

#### Scenario: A failing promise marker triggers recovery

- **WHEN** completion matches a promise marker whose projected Action output is `{ "promise": "FAIL" }`, `failIf` marks the task failed, and recovery declares `when: promise=FAIL`
- **THEN** recovery SHALL match the `promise` field
- **AND** the declared recovery tasks and `retrySelf` behavior SHALL be scheduled according to the remaining budget

#### Scenario: No matching recovery preserves expectation failure

- **WHEN** completion fails because an expectation is missing or `failIf` matched and no recovery handler matches the final output
- **THEN** the task SHALL remain failed
- **AND** no recovery task SHALL be added

### Requirement: Required-file projections come from task-level completion policy

Any task status or evidence projection that lists completion-required files SHALL derive them from top-level `expect`, not from Action Input. File-backed marker paths and `expect.files` paths SHALL remain inspectable as required files. The `_output` sentinel MUST NOT be exposed as a fetchable file path.

#### Scenario: File requirements appear in task evidence

- **WHEN** a task declares expected files and file-backed markers at task level
- **THEN** task evidence SHALL list those paths as completion requirements
- **AND** it SHALL NOT require the same declarations under `with`

#### Scenario: `_output` is not projected as a file

- **WHEN** a task declares a marker with `path: _output`
- **THEN** task evidence SHALL treat it as a turn-text requirement
- **AND** it MUST NOT offer `_output` as file content to fetch
