## REMOVED Requirements

### Requirement: Isolated temporary landing workspaces for branch-stable delivery

**Reason:** Landing workspaces are removed. Delivery now happens entirely on the single workflow workspace via `integrate:rebase` (with squash) followed by a fast-forward `integrate:push`, and the merge-ready preflight no longer needs a working-tree probe. The run branch is the single source of truth and is operated on directly; no second branch context is required.

**Migration:** `createLandingWorkspace`, `disposeLandingWorkspace`, and `pruneLandingWorkspaces` are deleted from `WorkspaceManager`, along with the landing path helpers and all landing calls in the action registry. Publish's landing-based squash-merge is replaced by on-workspace rebase + squash (see merge-delivery) and a fast-forward push; the merge-ready preflight switches to `merge-base --is-ancestor` (see MODIFIED "Read-only squash mergeability preflight"). Branch-stable delivery is now guaranteed by the workspace health gate (see workspace-health-gate) rather than by isolating landing operations in a disposable clone.

### Requirement: Authoritative final squash merge diagnostics

**Reason:** Integrate no longer runs a `git merge --squash` operation; the single-commit landing is produced by rebase's squash phase, and the remote landing is a fast-forward push. There is no "real Integrate squash merge" left to treat as authoritative.

**Migration:** Authoritative conflict reporting now belongs to the rebase task, covered by the merge-delivery "Rebase reconciles and squashes the run branch onto the base branch" requirement and its conflict-resolution scenarios. When a candidate passes the cheaper `is-ancestor` preflight but the Integrate rebase still produces conflicts, the rebase task reports the structured conflict evidence.

## MODIFIED Requirements

### Requirement: Read-only squash mergeability preflight

`WorktreeManager` SHALL provide a mergeability preflight that verifies whether an issue candidate is prepared against the latest base branch using `git merge-base --is-ancestor origin/<baseBranch> <runBranch>`, without mutating the base branch, the issue branch, or the workflow workspace branch context. The preflight SHALL be ref-safe and working-tree-free: it SHALL NOT check out the base branch inside the workflow workspace, it SHALL NOT create an isolated landing clone, it SHALL NOT run a `git merge --squash` probe, and it SHALL leave the workflow workspace on its `workspace.branch`. A candidate whose run branch already contains the latest remote base branch tip SHALL be reported as merge-ready; a candidate whose run branch does not contain the latest base tip SHALL be reported as not ready, indicating a rebase is required. Conflict file detail SHALL NOT be reported by the preflight; conflicts are surfaced by the authoritative Integrate rebase.

#### Scenario: Prepared candidate reports merge-ready

- **GIVEN** a run branch whose history already contains the latest `origin/<baseBranch>` tip
- **WHEN** Mohist checks mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles` (empty), and `checkedAt`
- **AND** the base branch, issue branch, and workflow workspace branch refs SHALL remain unchanged

#### Scenario: Candidate behind base reports not ready

- **GIVEN** a run branch whose history does NOT contain the latest `origin/<baseBranch>` tip
- **WHEN** Mohist checks mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL indicate a rebase is required
- **AND** the result SHALL NOT report conflict file detail from a probe merge

#### Scenario: Preflight does not check out the base branch or create a landing clone

- **WHEN** the mergeability preflight runs against an active workflow workspace
- **THEN** the preflight SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace
- **AND** the preflight SHALL NOT create an isolated landing clone or run a `merge --squash` probe
- **AND** the workflow workspace SHALL remain on `workspace.branch` before and after the preflight
