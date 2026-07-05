### Requirement: Single consolidated plan-artifacts check

The `mohist/local` workflow profile SHALL gate the plan stage on openspec artifacts through exactly one stage check, named `plan-artifacts`, that `uses: mohist/openspec-artifacts` with `changeDir: ${{ openspecChangeDir }}`. The profile SHALL NOT declare the per-artifact `core/artifact-exists` checks `proposal-complete`, `specs-complete`, `design-complete`, or `tasks-valid` in the plan stage. The consolidated check is the final gate over the plan artifacts and complements (does not replace) the per-task `expect.files` declarations that the engine validates on each plan task's completion.

#### Scenario: Local plan stage declares exactly one artifact gate

- **WHEN** the `mohist/local` workflow definition is loaded
- **THEN** the plan stage `checks` SHALL contain exactly one entry that gates openspec artifacts
- **AND** that entry SHALL have `name: plan-artifacts` and `uses: mohist/openspec-artifacts`
- **AND** that entry SHALL bind `changeDir` to `${{ openspecChangeDir }}`

#### Scenario: Per-artifact artifact-exists dispatches are removed

- **WHEN** the `mohist/local` plan stage checks are enumerated
- **THEN** the check names SHALL NOT include `proposal-complete`
- **AND** SHALL NOT include `specs-complete`
- **AND** SHALL NOT include `design-complete`
- **AND** SHALL NOT include `tasks-valid`

#### Scenario: Local profile mirrors the github-pr profile

- **WHEN** the `mohist/local` and `mohist/github-pr` plan-stage artifact gates are compared
- **THEN** both profiles SHALL gate plan artifacts through a single `plan-artifacts` check using `mohist/openspec-artifacts`
- **AND** neither profile SHALL declare a per-artifact `core/artifact-exists` dispatch for `proposal.md`, `specs/`, `design.md`, or `tasks.json`

### Requirement: Required plan artifact set includes the specs directory

The `mohist/openspec-artifacts` action SHALL treat the plan artifact set as four required entries under the resolved `changeDir`: `proposal.md` (file), `specs/` (directory), `design.md` (file), and `tasks.json` (file). The `specs/` directory SHALL be required: its absence SHALL cause the check to fail. The previous "specs is optional" behavior is retired. The action SHALL distinguish file presence from directory presence, so a path that exists as the wrong kind (e.g. `specs` is a file, `proposal.md` is a directory) SHALL count as missing.

#### Scenario: All four artifacts present succeeds

- **WHEN** `mohist/openspec-artifacts` runs with a `changeDir` containing `proposal.md` as a file, `specs/` as a directory, `design.md` as a file, and `tasks.json` as a file
- **THEN** the action SHALL return `status: success`
- **AND** the action output SHALL report `present: true` and `missing: []`

#### Scenario: Missing specs directory fails

- **WHEN** `mohist/openspec-artifacts` runs with `proposal.md`, `design.md`, and `tasks.json` present but no `specs/` directory under `changeDir`
- **THEN** the action SHALL return `status: failure`
- **AND** the action output SHALL report `present: false`
- **AND** `missing` SHALL contain the `specs/` path under `changeDir`

#### Scenario: Wrong kind counts as missing

- **WHEN** a required file path exists as a directory, or a required directory path exists as a file
- **THEN** the action SHALL treat that entry as missing
- **AND** the action SHALL include its path in the failure `missing` list

#### Scenario: Missing changeDir fails fast

- **WHEN** `mohist/openspec-artifacts` runs without a non-empty `changeDir` input
- **THEN** the action SHALL return `status: failure`
- **AND** the message SHALL state that the check requires `changeDir`
- **AND** the action SHALL NOT emit an `openspec-artifacts` structured output

### Requirement: Failure reports every missing artifact by path

When one or more required artifacts are absent, the `mohist/openspec-artifacts` action SHALL name every missing artifact by its path, both in the human-readable failure `message` and in the structured action `output`. The structured output SHALL be a JSON object with `kind: "openspec-artifacts"`, the resolved `changeDir`, `present: false`, and a `missing` array listing each missing path. The failure `message` SHALL contain every path listed in `missing`, so the single consolidated row stays as actionable as the four per-artifact rows it replaces.

#### Scenario: Single missing artifact is named

- **WHEN** exactly one of the four required artifacts is absent
- **THEN** the failure `message` SHALL contain that artifact's path
- **AND** `output.missing` SHALL be an array containing exactly that path

#### Scenario: Multiple missing artifacts are all named

- **WHEN** more than one required artifact is absent
- **THEN** the failure `message` SHALL contain every missing artifact's path
- **AND** `output.missing` SHALL list every missing artifact's path
- **AND** the check SHALL NOT stop at the first missing artifact

#### Scenario: Success output shape

- **WHEN** all four required artifacts are present
- **THEN** the action output SHALL be a JSON object with `kind: "openspec-artifacts"`, the resolved `changeDir`, `present: true`, and `missing: []`

### Requirement: Self-review and health checks remain separate

The `self-review-passed` (plan quality gate) and `health` (formatting gate) checks SHALL remain as distinct stage checks in the `mohist/local` plan stage, separate from the consolidated `plan-artifacts` check. The `plan-artifacts` check SHALL verify only the presence and kind of the four plan artifacts and SHALL NOT evaluate self-review promise markers or `git diff --check` output. `self-review-passed` SHALL continue to gate on the `<promise>PASS</promise>` marker in `self-review.md`, and `health` SHALL continue to run `git diff --check`.

#### Scenario: Plan stage retains three distinct gates

- **WHEN** the `mohist/local` plan stage checks are enumerated
- **THEN** the checks SHALL include `plan-artifacts`, `self-review-passed`, and `health` as distinct entries
- **AND** `self-review-passed` SHALL remain a `core/marker` check over `self-review.md`
- **AND** `health` SHALL remain a `core/script` check running `git diff --check`

#### Scenario: Artifact gate does not evaluate quality or formatting

- **WHEN** the `plan-artifacts` check runs
- **THEN** it SHALL only verify the presence and kind of `proposal.md`, `specs/`, `design.md`, and `tasks.json`
- **AND** it SHALL NOT read or assert the contents of `self-review.md`
- **AND** it SHALL NOT invoke `git diff --check`
