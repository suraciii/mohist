### Requirement: Rebase recovery has a single author in workflow content
The recovery definition for the API-triggered rebase task SHALL be authored in workflow content as the single source of truth. HTTP route helpers SHALL NOT construct `RecoveryDefinition`, `RecoveryHandlerDefinition`, `TaskDefinition`, or any other workflow-definition structure. The Action `uses` identifier and prompt references within the rebase recovery SHALL be declared in workflow content, not hardcoded in C# route code.

#### Scenario: IssueRoutes.Helpers does not build recovery definitions
- **WHEN** the rebase route handles a request
- **THEN** the route SHALL NOT call any helper that constructs a `RecoveryDefinition` in C#
- **AND** the route SHALL NOT reference the literal `mohist/opencode` as a recovery task `uses` value

### Requirement: Rebase recovery mechanism is documented as one or two concepts
`design/workflow/recovery.md` SHALL state whether API-triggered one-shot task injection and Profile-driven recovery are the same mechanism with two entry points or two distinct concepts. If they are one mechanism, the rebase recovery definition SHALL move into builtin workflow content. If they are two concepts, one-shot task injection SHALL have its own name and representation and SHALL NOT reuse `RecoveryDefinition`.

#### Scenario: Same mechanism — recovery moves to workflow content
- **WHEN** the design concludes one-shot injection and Profile recovery are the same mechanism
- **THEN** the rebase recovery definition SHALL be authored in builtin workflow content
- **AND** the rebase route SHALL reference that definition rather than constructing one

#### Scenario: Two concepts — one-shot injection gets its own representation
- **WHEN** the design concludes one-shot injection is a distinct concept
- **THEN** the one-shot task injection SHALL have a representation distinct from `RecoveryDefinition`
- **AND** SHALL NOT overload recovery's budget or handler semantics

### Requirement: Rebase visible behavior is preserved
The user-visible behavior of `mo issue rebase` SHALL NOT change. The same recovery task SHALL be produced with the same ordering, the same `uses` Action, and the same conflict-resolution prompt. The rebase task SHALL remain a `mohist/rebase` task with a `mohist/opencode` conflict-resolution recovery handler.

#### Scenario: Rebase produces the same recovery task
- **WHEN** a caller triggers `mo issue rebase` for an issue with a workflow run
- **THEN** a rebase task SHALL be queued with a recovery handler that runs conflict resolution
- **AND** the recovery handler's task SHALL use `mohist/opencode` with the rebase-conflict prompt
- **AND** the task ordering and recovery budget SHALL match the behavior before this change
