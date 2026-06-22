## Why

Today every completed change is squash-pushed directly onto the base branch, leaving no visible, traceable integration unit on GitHub — merges are invisible, unverifiable, and irreversible only by hand. We need an optional workflow that routes each AI-produced change through a public GitHub PR so the trunk always advances in atomic, rollback-able steps, without depending on GitHub-side branch protection or CI and without moving Mohist's existing approval gate onto GitHub.

## What Changes

- Add a new built-in workflow profile `mohist/pr`, selectable at project and issue level, that runs the same `plan → build → check → integrate` pipeline and the same approval gates as `mohist/default`. The only difference lives in the integrate delivery task.
- Replace the integrate delivery task for `mohist/pr`: instead of `mohist/publish` (local squash + fast-forward push to base), use a new `mohist/publish-via-pr` runner action. The single-commit-on-base invariant is preserved via GitHub's squash merge, not by local squashing. `integrate:prepare` (rebase + conflict resolution) runs unchanged — its existing rebase body stays, only the local squash disappears because squash now happens at GitHub merge time.
- `mohist/publish-via-pr` is a three-step idempotent action: (1) `git push --force-with-lease origin <workspace.branch>`; (2) open or reuse the existing open PR for `head:base`; (3) merge the PR (default squash) if not already merged. Re-attempts and runner-lost recovery MUST NOT create duplicate PRs, fail on an existing branch, or error on an already-merged PR.
- All PR operations are performed via the `gh` CLI (no GitHub HTTP API client, no token stored by Mohist). Runner hosts MUST have `gh` installed and `gh auth login` completed; missing `gh` is a fail-fast `config-error`.
- Extend delivery failure classification with PR-specific kinds: `base-moved` (workflow integrate retry converges by re-fetch + rebase + force-push), `retry-safe` (network/rate-limit backoff), `config-error` (gh missing/unauthenticated — no retry), `protection-conflict` (branch protection requires checks/reviews — human, no retry), `pr-state-conflict` (PR closed or state changed externally — human). No rebase loop inside the action — base movement is handled by workflow-level integrate retry, relying on the already-idempotent spec-sync/archive/rebase.
- Record `prNumber`, `prUrl`, and `mergeCommitSha` on the publish task's structured output via the existing TaskRun output mechanism (no schema change). PR merge confirmed `state=merged` is the workflow completion signal.
- PR metadata conventions: title `Complete issue #N`; body `Mohist issue #N` (no `Closes #N` — issue lifecycle stays owned by Mohist); squash merge commit message `Complete issue #N` via `gh pr merge --subject` (matches the existing `mohist/default` direct-push commit message).
- Web UI: on the detail page of an issue completed via `mohist/pr`, show "经由 PR #N 合并" with a link to the GitHub PR; do not show it for issues completed via `mohist/default` or unfinished issues.
- Remote feature-branch cleanup is explicitly NOT a Mohist responsibility; it relies on the GitHub repo's "Automatically delete head branches" setting. **BREAKING**: none — `mohist/pr` is additive and coexists with `mohist/default`.

## Capabilities

### New Capabilities
- `github-pr-delivery`: The `mohist/publish-via-pr` runner action contract — three ordered steps (force-with-lease push, open-or-reuse PR, merge), idempotency across retries and runner-lost recovery, `gh` CLI prerequisite with fail-fast `config-error`, PR-specific failure classification (`base-moved` / `retry-safe` / `config-error` / `protection-conflict` / `pr-state-conflict`), PR title/body/merge-message conventions, and `prNumber` / `prUrl` / `mergeCommitSha` structured outputs.
- `pr-delivery-indicator`: Web UI surface on the issue detail page that, for issues completed via `mohist/pr`, renders "经由 PR #N 合并" with a link to the GitHub PR, and is absent for issues completed via `mohist/default` or for unfinished issues.

### Modified Capabilities
- `workflow-config`: The built-in profile registry SHALL expose `mohist/pr` alongside `mohist/default` as a second selectable built-in profile; project- and issue-level profile selection, override, and listing SHALL apply identically to both.
- `workflow-definition`: A built-in `mohist/pr` workflow definition SHALL be declared that differs from `mohist/default` ONLY in the integrate delivery task (`mohist/publish` → `mohist/publish-via-pr`); plan/build/check tasks, approval gates, repair policy, and integrate ordering (spec-sync → archive-change → prepare → publish) SHALL remain identical.
- `merge-delivery`: The delivery contract SHALL admit two delivery shapes — the existing direct shape (local squash + fast-forward push) and a new PR-based shape (force-push branch, open/reuse PR, merge via GitHub). The single-commit-on-base invariant is preserved in both. Failure classification is extended with PR-specific kinds; clean-workspace-on-failure and run-branch-stability invariants from direct delivery apply equally to PR delivery.
- `workflow-run`: The publish task's structured output SHALL additionally carry `prNumber`, `prUrl`, and `mergeCommitSha` for PR-based delivery, alongside the existing `targetBranch` / `baseSha` / landed-commit / pushed fields.

## Impact

- **Server (C#)**: `IssueWorkflowProfileRegistry`, `IssueWorkflowProfiles`, `MohistWorkflow`, and the `mohist-default.workflow.yaml` neighborhood gain a parallel `mohist/pr` profile + YAML resource and registration. Profile listing / description / `suitableFor` surfaces (Settings Workflows tab, `mo workflow list --described`, issue creation recommended_workflow resolution) pick up the new profile automatically.
- **Runner (TypeScript)**: New `mohist/publish-via-pr` action registered in `actions/registry.ts` alongside `mohist/publish`; reuses the existing `ActionContext`, landing-workspace manager (if needed), and `ActionResult` shape. Adds `gh` CLI detection as a fail-fast prerequisite. No GitHub HTTP API client or token storage.
- **Web (React)**: Issue detail page reads the publish task's PR metadata from the existing task-result read model and renders the PR indicator conditionally.
- **External dependency**: Runner hosts must have `gh` CLI installed and authenticated — a one-time environment setup of the same class as configuring git SSH keys / credential helpers. Documented as a prerequisite; absence is a fail-fast `config-error`, not a runtime retry.
- **Existing behavior**: `mohist/default` direct-push delivery is unchanged. Existing issues, runs, and repos continue to behave identically. No database migration; no breaking API change.
- **Out of scope**: GitHub Actions / CI integration, GitHub issue sync, GitHub-side human review/approval, branch-protection rules, required status checks, and remote head-branch deletion (GitHub auto-delete setting). No rebase loop inside `mohist/publish-via-pr` — base movement is converged by workflow-level integrate retry.
