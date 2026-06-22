## Context

Mohist today ships a single built-in workflow profile, `mohist/default`, whose integrate stage squash-merges the working branch locally and fast-forward pushes one commit onto `base`. There is no visible integration unit on GitHub. The runner already supports a generic `failureKind` channel in `ActionResult.output` JSON (`base-moved`, `retry-safe`, `conflict`) consumed by CLI/web renderers; the server stores task `output` verbatim and does not branch on it. The profile system (`IssueWorkflowProfileRegistry`, `MohistIssueWorkflowProfileBase`, `MohistWorkflow.cs`) loads a single bundled YAML resource (`mohist-default.workflow.yaml`) and exposes one profile. The web issue-detail page already reads `WorkflowRun` task results through a read model.

Constraints:
- Issue lifecycle, approvals, plan/build/check stages, and the `integrate:spec-sync` → `archive-change` → `prepare` → `publish` ordering are unchanged.
- No new DB schema, no breaking API change, no GitHub HTTP client, no Mohist-managed GitHub tokens.
- `gh` CLI availability is a host-level prerequisite of the same class as git SSH keys.
- Stakeholders: AI runner (executes the action), server (registers profile + serializes task results), web (renders PR indicator), operator (installs `gh`).

## Goals / Non-Goals

**Goals:**
- Ship `mohist/pr` as a second built-in profile that differs from `mohist/default` only in the integrate delivery task.
- Ship `mohist/publish-via-pr` runner action: 3-step idempotent push / open-or-reuse / merge via `gh` CLI, with extended failure classification.
- Surface PR metadata (`prNumber`, `prUrl`, `mergeCommitSha`) through the existing `TaskRun.output` channel and render it on the issue detail page for `mohist/pr` issues only.
- Preserve `mohist/default` behavior byte-for-byte; no migration, no breaking change.

**Non-Goals:**
- GitHub Actions / CI integration (Epic #18 follow-up).
- GitHub issue sync, GitHub-side human review/approval, branch protection rules, required status checks.
- Remote head-branch deletion (GitHub repo auto-delete setting).
- An action-internal rebase loop; `base-moved` converges via workflow-level integrate retry.
- Per-stage or per-task profile overrides; the profile selection unit is the whole workflow.

## Decisions

### D1. New profile via subclass + parallel YAML resource (not generic data-driven registration)

Add `MohistPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase` overriding `Id`/`DisplayName`/`Description`/`IsDefault=false`/`SuitableFor`/`Definition`. Extend `MohistWorkflow.cs` with a second `Lazy<WorkflowDefinition> PrDefinition` loading a new `mohist-pr.workflow.yaml` resource (sibling file, same csproj `<EmbeddedResource>`/copy pattern). Register both in `IssueWorkflowProfileRegistry`.

**Alternatives considered:**
- Generic "register profile from YAML path" API — rejected: only two built-ins needed today, YAGNI; the subclass mirrors the existing `MohistDefaultIssueWorkflowProfile` shape and keeps `SuitableFor`/`DisplayName` strongly typed.
- Constructing `mohist/pr` definition programmatically by deep-cloning `mohist/default` and patching the publish task in C# — rejected: a real YAML file is auditable, diff-able, and stays declaratively consistent with the spec contract that the two definitions are identical except for one task.

### D2. `mohist-pr.workflow.yaml` is a verbatim copy with one task swap

The YAML duplicates `mohist-default.workflow.yaml` and changes exactly one line: `integrate:publish`'s `uses: mohist/publish` becomes `uses: mohist/publish-via-pr`. The `with` block keeps `source: ${{ workspace.branch }}`, `target: ${{ repository.baseBranch }}`, `remote: origin`, `message: "Complete issue #${{ issue.number }}"`. The new action consumes the same inputs.

**Alternatives considered:**
- Adding a `delivery.mode: pr` knob interpreted by `mohist/publish` — rejected: violates single-responsibility, forces one action to branch on shape, complicates failure classification.
- YAML inheritance (`extends: mohist-default`) — rejected: would require new serializer features; keep YAML flat for v1, revisit if a third profile appears.

### D3. New runner action file `actions/publish-via-pr.ts`, registered alongside `mohist/publish`

The action lives in its own file (the existing `publishAction` is inlined in `registry.ts`; we do not refactor that for v1). It reuses `ActionContext`, `ActionResult`, the `git` helper, and `runCommand` (for `gh`). It writes structured output with `kind: "publish-via-pr"` and a `failureKind` union extended to `"base-moved" | "retry-safe" | "config-error" | "protection-conflict" | "pr-state-conflict" | undefined`.

Internal ordering:
1. **Precheck**: `gh --version` then `gh auth status`. Non-zero → `config-error` with operator-facing instructions. Done before any remote mutation.
2. **Push**: `git push --force-with-lease origin <workspace.branch>` from the workflow workspace (not a landing workspace — no local squash, no base checkout).
3. **Open/reuse PR**: `gh pr list --head <branch> --base <target> --state open --json number,url`. Empty → `gh pr create --title "Complete issue #N" --body "Mohist issue #N" --head <branch> --base <target>`. Non-empty → reuse row 0.
4. **Merge or confirm**: `gh pr view <N> --json state,mergeCommit,url`. `state=merged` → success with recorded metadata, no merge call. Otherwise `gh pr merge <N> --squash --subject "Complete issue #N"`. Re-read `state` to confirm `merged` and capture `mergeCommit.oid` as `mergeCommitSha`.

**Alternatives considered:**
- GitHub HTTP API client (octokit) — rejected (issue body rationale): token storage surface, one-order-of-magnitude more code, diverges from runner's `git`/`dotnet`/`node` shell-out pattern.
- Performing the merge inside the landing workspace used by `mohist/publish` — rejected: PR-based delivery has no local landing commit; the workflow workspace staying on `workspace.branch` and pushing that branch directly is the correct invariant.
- Suppressing `--squash` and letting GitHub's repo-default merge method decide — rejected: spec requires the single-commit-on-base invariant; `--squash` makes it explicit and matches `mohist/default`.

### D4. Failure classification lives entirely in runner `output.failureKind`; no server schema change

The `failureKind` field is already a free-form-ish string in `TaskRun.output` JSON; CLI/web renderers switch on it. We extend the runner-side union and add rendering for the three new kinds (`config-error`, `protection-conflict`, `pr-state-conflict`) alongside the existing `base-moved`/`retry-safe`/`conflict` rendering. The server stores and forwards `output` verbatim.

Classification rules:
- `gh` missing or `gh auth status` non-zero → `config-error`.
- `gh pr merge` stderr matches "Merge conflict" / "not mergeable" / base moved signals → `base-moved`.
- `gh pr merge` stderr matches "protected branch", "required status check", "review required" → `protection-conflict`.
- PR `state=closed` observed mid-action, or `gh pr view` returns `CLOSED`/`MERGED` unexpectedly between steps → `pr-state-conflict`.
- Network/rate-limit (`gh` exit 1 with "rate limit", DNS errors, timeouts) → `retry-safe`.

### D5. PR metadata rides on `TaskRun.output`, not a new column

The action's success output JSON includes `prNumber`, `prUrl`, `mergeCommitSha` alongside `targetBranch`, `baseSha`, `pushed`. The existing `WorkflowRun` task-result read model already serializes `output`. The web `PrDeliveryIndicator` component reads these fields conditionally on `output.kind === "publish-via-pr"` and issue delivered state. No DB migration, no new API endpoint.

### D6. `base-moved` converges via existing workflow integrate retry, not an action-internal loop

`mohist/publish-via-pr` reports `base-moved` and stops. Workflow retry re-runs the integrate stage tasks from the failed point; because `spec-sync`/`archive-change`/`prepare` are already idempotent and re-entrant, prepare re-fetches and rebases, the next publish-via-pr force-pushes the rebased branch (making the PR mergeable), and merge succeeds. This mirrors how `mohist/default`'s `base-moved` is recovered today and requires no new invalidation policy. The action explicitly does NOT contain a rebase loop.

**Alternatives considered:**
- Action-internal rebase loop on `base-moved` — rejected: duplicates the conflict resolver, hides progress from the StageRun view, and breaks the "prepare is the single place conflict resolution happens" invariant.
- Adding an invalidation rule that auto-reruns `prepare` when `publish` reports `base-moved` — deferred: existing manual/auto workflow retry already covers this for v1; revisit if telemetry shows friction.

### D7. Web indicator is a small leaf component reading existing read-model fields

Add `PrDeliveryIndicator` (or extend the existing delivery-summary component) on the issue detail page. It reads the publish task's `output.prNumber` and `output.prUrl` and renders "经由 PR #N 合并" with a link. Conditional rendering: (a) issue is in delivered state, (b) publish task result `output.kind === "publish-via-pr"`. No new API route, no GitHub API call from the browser.

## Risks / Trade-offs

- **`gh` CLI prerequisite operators forget** → fail-fast `config-error` at action start with explicit "install gh + run gh auth login" message; documented in `mohist/pr` profile description and `suitableFor`.
- **GitHub rate limits under high issue throughput** → `gh`'s built-in backoff plus action-level `retry-safe` classification plus workflow-level retry. Acceptable for v1 single-user / small-team target.
- **Token scope insufficient for PR write** → `gh auth status` may pass but `gh pr create` fails; classify as `config-error` (treat as environment problem) rather than `retry-safe`, since retry without re-auth won't help.
- **Two built-in YAMLs drift over time** → keep `mohist-pr.workflow.yaml` as a thin variant; add a unit test asserting the two definitions are identical modulo the publish task. If a third profile appears, invest in YAML inheritance.
- **Concurrent integrate on the same base across projects** → existing `lockBehavior: sequential` + `resources: [project-integration]` already serializes; `--force-with-lease` only ever overwrites the same runner's own prior push, so cross-project interference surfaces as `pr-state-conflict` to a human, not silent corruption.
- **PR left open after repeated merge failures** → visible on GitHub; mohist surfaces the failure to a human via existing task-failure UI. Auto-close-on-failure is explicitly out of scope (would discard useful state).
- **`gh pr merge --squash` default body includes Co-authored-by trailers** → acceptable for v1; if undesired later, pass `--body ""` explicitly. Non-blocking.
- **Branch protection on `base` blocks merge** → classified `protection-conflict`, surfaced to human. Documented as a configuration mismatch, not a Mohist bug.

## Migration Plan

**Deploy:**
1. Ship server build containing `MohistPrIssueWorkflowProfile`, `mohist-pr.workflow.yaml`, and registry registration. Existing `mohist/default` issues unaffected.
2. Ship runner build containing `mohist/publish-via-pr` action and `gh` precheck. Existing `mohist/publish` unaffected.
3. Ship web build containing `PrDeliveryIndicator`. Old issues (no PR metadata) render without the indicator.
4. Operator documents the `gh` CLI prerequisite alongside existing git-SSH-key setup; no automated install.

**Order:** server and runner must be deployed together before any project selects `mohist/pr`. A runner without the new action will fail `mohist/pr` integrate with "unknown action"; a server without the new profile will fail project/issue profile selection with "profile not found".

**Rollback:**
1. Revert server: `mohist/pr` disappears from the registry. Projects/issues using it surface "profile not found" at next workflow action; switch the issue's profile to `mohist/default` to recover.
2. Revert runner: action disappears. Same recovery as above.
3. Revert web: indicator disappears; issue detail renders without it.
4. No DB columns or API contracts to roll back. Existing `mohist/default` runs are untouched at every step.

## Open Questions

- **PR body richness**: Issue body mandates minimal body (`Mohist issue #N`). Do we want to include a deep link back to the Mohist issue detail page in a future iteration? Defer to a follow-up; v1 keeps the body minimal.
- **Pre-start validation**: Should we add a `mohist/pr`-specific startup check that fails an issue at "start" time (rather than at integrate) when `gh` is missing? Better UX but adds profile-specific startup hooks. Defer to a follow-up.
- **PR description for multi-attempt runs**: If the same PR is reused after a `base-moved` retry, should the action post a comment summarizing the re-attempt? Not required for v1; nice-to-have for observability under Epic #18.
- **Indicator placement**: Whether the "经由 PR #N 合并" indicator belongs at the issue-detail header, in the activity timeline, or in a delivery-summary section. Decision left to the design.md follow-up or the implementing task; spec only requires visibility without drilling into logs.
