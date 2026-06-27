## MODIFIED Requirements

### Requirement: Base-moved recovery preserved

When `mohist/merge-github-pr` fails with `errorCode: base-moved`, the profile SHALL insert recovery tasks that execute `mohist/rebase` (`recover:rebase`), then `mohist/push` (`recover:push`), and then `retry: self` of the original `merge-github-pr` task. Recovery SHALL reuse the same workflow branch and the same open PR; it SHALL NOT open a replacement PR and SHALL NOT re-mark the PR ready. When conflicts occur during `recover:rebase`, `mohist/rebase` SHALL return `output.failureKind: conflict` and SHALL leave the rebase in progress; the profile SHALL then resolve conflicts via an explicit `recover:resolve-rebase-conflicts` agent task declared under `recover:rebase.onFailure`. The conflict-resolution task SHALL NOT trigger a retry of `recover:rebase`; after it completes successfully the workflow SHALL continue directly to `recover:push` so that the conflict-resolution agent's completed rebase is preserved, since a retry of `recover:rebase` would abort the in-progress rebase, destroy the agent's resolved work, and re-hit the same conflict until the recovery budget is exhausted. The `mohist/rebase` action SHALL delegate conflicts to a task by default and the profile SHALL NOT declare a `conflictMode` field for it. The `recover:push` task SHALL push the single-owner dynamic workflow branch (`mohist/run-<runId>`) using the `mohist/push` action's `force: true` mode (`--force`), because dynamic branches carry no remote-tracking ref and bare `--force-with-lease` always fails on them; the check-stage regular push SHALL continue to use `forceWithLease: true` since it performs no rebase rewriting.

#### Scenario: Base moved triggers rebase recovery

- **WHEN** `mohist/merge-github-pr` fails with `errorCode: base-moved`
- **THEN** the profile SHALL insert recovery tasks in order: `recover:rebase`, `recover:push`
- **AND** SHALL append a fresh attempt of the original `merge-github-pr` task via `retry: self`

#### Scenario: Recovery reuses same branch and PR

- **WHEN** the `base-moved` recovery tasks execute
- **THEN** they SHALL push the same workflow branch and update the same open PR
- **AND** SHALL NOT create a new PR
- **AND** SHALL NOT re-mark the PR ready

#### Scenario: Rebase conflict delegates to resolution task without retrying rebase

- **WHEN** `recover:rebase` runs and a conflict occurs
- **THEN** `mohist/rebase` SHALL return `output.failureKind: conflict` and SHALL leave the rebase in progress
- **AND** the profile SHALL execute `recover:resolve-rebase-conflicts` declared under `recover:rebase.onFailure`
- **AND** the conflict-resolution task SHALL NOT trigger a retry of `recover:rebase`
- **AND** after the conflict-resolution task completes successfully the workflow SHALL continue directly to `recover:push` and then retry the merge

#### Scenario: Rebase requires no conflictMode declaration

- **WHEN** `recover:rebase` is declared in the profile
- **THEN** the profile SHALL NOT include a `conflictMode` field in the rebase `with` block
- **AND** `mohist/rebase` SHALL delegate conflicts to a task by default behavior

#### Scenario: Recovery push uses force mode for dynamic branch

- **WHEN** `recover:push` executes as part of the `base-moved` recovery
- **THEN** it SHALL push the dynamic workflow branch using the `mohist/push` action with `force: true` (`--force`)
- **AND** SHALL NOT use `forceWithLease: true` for the post-rebase recovery push

#### Scenario: Check-stage push keeps force-with-lease

- **WHEN** a regular `mohist/push` task executes in the check stage
- **THEN** it SHALL continue to use `forceWithLease: true`
- **AND** SHALL NOT switch to `force: true`, because the check stage performs no rebase rewriting
