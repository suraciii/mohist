import { defineAction } from "./define-action.js"
import type { ActionDefinition } from "./manifest.js"
import type { ActionHost } from "./host.js"
import { opencodeAction } from "./opencode.js"
import { piAction } from "./pi.js"
import {
  createGitHubPrAction,
  markGitHubPrReadyAction,
  mergeGitHubPrAction,
} from "./github-pr.js"
import { githubPrChecksAction } from "./github-pr-checks-action.js"
import { githubPrStatusAction } from "./github-pr-status.js"
import {
  archiveChangeAction,
  openspecArtifactsAction,
  openspecTasksAction,
} from "./openspec.js"
import { rebaseAction, rebaseStatusAction } from "./rebase.js"
import { mergeReadyAction } from "./merge-ready.js"
import { pushAction } from "./push.js"
import { workspacePrepareAction } from "./workspace-prepare.js"
import {
  processAction,
  scriptAction,
  artifactExistsAction,
  markerAction,
} from "./built-in-core.js"

export const ACP_AGENT_TOMBSTONE = {
  name: "mohist/acp-agent",
  guidance:
    "Workflow task uses the removed Action 'mohist/acp-agent'. The Action no longer exists in this runner. " +
    "Rerun the affected stage with a profile that uses 'mohist/opencode' to recover this run.",
} as const

export const BUILT_IN_ACTION_TOMBSTONES = [ACP_AGENT_TOMBSTONE]

export const builtInActions: ReadonlyArray<ActionDefinition> = [
  defineAction({
    manifest: {
      name: "core/process",
      description: "Run a process against the worktree and capture stdout/exit code",
      inputs: {
        command: { types: ["string"], required: true, description: "Command to invoke" },
        args: { types: ["array"], default: [], description: "Arguments passed to the command" },
      },
      outputs: [
        { name: "stdout", description: "Trimmed command stdout" },
        { name: "exitCode", description: "Process exit code" },
      ],
      errors: [{ code: "process-failed", description: "Process exited with a non-zero status" }],
    },
    run: processAction,
  }),
  defineAction({
    manifest: {
      name: "core/script",
      description: "Run an inline script through a per-platform shell wrapper",
      inputs: {
        run: { types: ["string"], required: true, description: "Script body" },
        shell: { types: ["string"], description: "Shell executable; defaults to bash or pwsh based on platform" },
        timeout: { types: ["number"], description: "Script execution timeout in milliseconds" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "run", description: "Echoed script body" },
        { name: "shell", description: "Resolved shell executable" },
        { name: "exitCode", description: "Shell exit code" },
        { name: "stdout", description: "Truncated stdout" },
        { name: "stderr", description: "Truncated stderr" },
      ],
      errors: [{ code: "script-failed", description: "Script exited with a non-zero status" }],
    },
    run: scriptAction,
  }),
  defineAction({
    manifest: {
      name: "core/artifact-exists",
      description: "Verify a workspace-relative artifact path exists",
      inputs: {
        path: { types: ["string"], required: true, description: "Path to verify" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "path", description: "Resolved path" },
        { name: "exists", description: "Whether the path exists" },
      ],
      errors: [{ code: "artifact-missing", description: "Required artifact is absent" }],
    },
    run: artifactExistsAction,
  }),
  defineAction({
    manifest: {
      name: "core/marker",
      description: "Match a marker inside a workspace-relative file",
      inputs: {
        path: { types: ["string"], required: true, description: "Path to read" },
        expect: { types: ["string"], description: "Marker text to match" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "path", description: "Resolved path" },
        { name: "marker", description: "Marker text the check matched against" },
        { name: "found", description: "Whether the marker was found" },
      ],
      errors: [
        { code: "artifact-missing", description: "Marker file is absent" },
        { code: "marker-missing", description: "Marker text not found in the file" },
      ],
    },
    run: markerAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/opencode",
      description: "Run an OpenCode agent turn",
      inputs: {
        prompt: { types: ["string", "object"], required: true, description: "Prompt string or structured prompt spec" },
        session: { types: ["string"], description: "Logical session name; falls back to work id when absent" },
        options: { types: ["object"], description: "Turn options such as model and variant" },
        timeout: { types: ["number"], default: 3600000, description: "Turn deadline in milliseconds" },
      },
      outputs: [
        { name: "promise", description: "Completion promise projected by the task executor" },
      ],
      errors: [
        { code: "runtime-unavailable", description: "The OpenCode runtime is unavailable" },
        { code: "session-workspace-mismatch", description: "AgentSession is bound to a different workspace" },
        { code: "session-binding-failed", description: "Failed to resolve or persist the AgentSession binding" },
        { code: "runtime-session-missing", description: "Runtime session is missing" },
        { code: "unavailable-runtime", description: "Runtime reported unavailable" },
        { code: "incompatible-runtime", description: "Runtime is incompatible with the request" },
        { code: "permission-required", description: "Permission is required to proceed" },
        { code: "interrupted", description: "The turn was interrupted" },
        { code: "turn-failed", description: "OpenCode turn failed for an unspecified reason" },
      ],
      capabilities: ["agent-turn"],
    },
    run: opencodeAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/pi",
      description: "Run a Pi agent turn",
      inputs: {
        prompt: { types: ["string", "object"], required: true, description: "Prompt string or structured prompt spec" },
        session: { types: ["string"], description: "Logical session name; falls back to work id when absent" },
        options: { types: ["object"], description: "Turn options such as model and variant" },
        timeout: { types: ["number"], default: 3600000, description: "Turn deadline in milliseconds" },
      },
      outputs: [
        { name: "promise", description: "Completion promise projected by the task executor" },
      ],
      errors: [
        { code: "runtime-unavailable", description: "The Pi runtime is unavailable" },
        { code: "session-workspace-mismatch", description: "AgentSession is bound to a different workspace" },
        { code: "session-binding-failed", description: "Failed to resolve or persist the AgentSession binding" },
        { code: "runtime-session-missing", description: "Runtime session is missing" },
        { code: "unavailable-runtime", description: "Runtime reported unavailable" },
        { code: "turn-failed", description: "Pi turn failed for an unspecified reason" },
      ],
    },
    run: (inputs, host: ActionHost) => piAction(inputs, host),
  }),
  defineAction({
    manifest: {
      name: "mohist/openspec-tasks",
      description: "Load tasks.json into the workflow as executable tasks",
      inputs: {
        path: { types: ["string"], required: true, description: "Path to tasks.json" },
        task: { types: ["object"], description: "Default task-level fields applied to each entry", render: "deferred" },
        items: { types: ["string"], default: "tasks", description: "Top-level items path inside the JSON document" },
        buildPrompt: { types: ["string"], engineSource: "prompts.build" },
      },
      outputs: [
        { name: "loaded", description: "Count of tasks loaded into the run" },
      ],
      errors: [
        { code: "missing-source", description: "tasks.json file is missing" },
        { code: "server-unavailable", description: "Server connection is unavailable" },
      ],
      capabilities: ["add-tasks"],
    },
    run: openspecTasksAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/openspec-artifacts",
      description: "Verify the required OpenSpec change artifacts exist",
      inputs: {
        changeDir: { types: ["string"], required: true, description: "Path to the OpenSpec change directory" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "changeDir", description: "Resolved change directory" },
        { name: "present", description: "Whether all required artifacts are present" },
        { name: "missing", description: "List of missing artifact paths" },
      ],
      errors: [{ code: "artifacts-missing", description: "Required OpenSpec artifacts are absent" }],
    },
    run: openspecArtifactsAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/archive-change",
      description: "Archive an OpenSpec change directory and commit the move",
      inputs: {
        changeDir: { types: ["string"], required: true, description: "Path to the OpenSpec change directory" },
        archiveHint: { types: ["string"], description: "Persisted archive destination (relative) from a prior run; when present and the destination still exists, the archive is treated as already complete" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "source", description: "Source change directory" },
        { name: "destination", description: "Archive destination directory" },
        { name: "destinationRel", description: "Archive destination relative to the workspace root" },
        { name: "changed", description: "Whether the archive step modified the repository" },
        { name: "noChange", description: "Whether the archive step produced no changes" },
        { name: "commitMessage", description: "Commit message when the archive step changed the repository" },
        { name: "commitSha", description: "Commit sha when the archive step changed the repository" },
        { name: "commitOutput", description: "Raw git commit output" },
        { name: "changedFiles", description: "Files changed by the archive commit" },
      ],
      errors: [
        { code: "retry-safe", description: "Archive step is safe to retry" },
        { code: "partial-archive", description: "Source and archive both contain files; refusing to overwrite" },
        { code: "missing-source", description: "Source change directory is missing" },
        { code: "config-error", description: "Archive configuration is invalid" },
      ],
      capabilities: ["write-vars"],
    },
    run: archiveChangeAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/rebase",
      description: "Rebase the current branch onto a base branch with optional squash",
      inputs: {
        baseBranch: { types: ["string"], required: true, description: "Base branch name" },
        remote: { types: ["string"], description: "Git remote name" },
        squash: { types: ["boolean"], default: false, description: "Squash the rebased commits into one" },
        message: { types: ["string"], description: "Literal squash commit message" },
        messageFrom: { types: ["string"], description: "Issue field source for the squash commit message" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Rebase status discriminator" },
        { name: "baseBranch", description: "Base branch name" },
        { name: "remote", description: "Git remote name" },
        { name: "baseRef", description: "Resolved base ref" },
        { name: "rebasedOntoSha", description: "Tip of the base ref at rebase start" },
        { name: "beforeHeadSha", description: "HEAD sha before rebase" },
        { name: "afterHeadSha", description: "HEAD sha after rebase" },
        { name: "squashed", description: "Whether the squash step ran" },
        { name: "squashedHeadSha", description: "HEAD sha after squash" },
        { name: "rebased", description: "Whether the rebase succeeded" },
        { name: "conflicts", description: "Files with unresolved conflicts" },
        { name: "rebaseLeftInProgress", description: "Whether a rebase was left in progress" },
        { name: "output", description: "Aggregated git output" },
        { name: "steps", description: "Per-step git command results" },
      ],
      errors: [
        { code: "abort-failed", description: "Failed to abort an existing rebase" },
        { code: "fetch-failed", description: "Failed to fetch the base branch" },
        { code: "base-resolve-failed", description: "Failed to resolve the base ref" },
        { code: "prepare-failed", description: "Failed to prepare the workspace before rebase" },
        { code: "rebase-failed", description: "Rebase failed for an unspecified reason" },
        { code: "conflict", description: "Rebase encountered conflicts" },
        { code: "squash-failed", description: "Squash step failed" },
      ],
      capabilities: ["issue-fields"],
    },
    run: rebaseAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/rebase-status",
      description: "Report the current rebase state of the worktree",
      inputs: {
        baseBranch: { types: ["string"], required: true, description: "Base branch name" },
        remote: { types: ["string"], description: "Git remote name" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Status discriminator (verified or failed)" },
        { name: "baseBranch", description: "Base branch name" },
        { name: "remote", description: "Git remote name" },
        { name: "baseRef", description: "Resolved base ref" },
        { name: "rebaseInProgress", description: "Whether a rebase is in progress" },
        { name: "conflicts", description: "Files with unresolved conflicts" },
        { name: "baseSha", description: "Tip of the base ref" },
        { name: "headSha", description: "Current HEAD sha" },
        { name: "mergeBaseSha", description: "Merge base of HEAD and base ref" },
        { name: "output", description: "Aggregated git output" },
      ],
      errors: [{ code: "rebase-incomplete", description: "Rebase is incomplete or not clean" }],
    },
    run: rebaseStatusAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/merge-ready",
      description: "Report whether the worktree can merge into the base branch",
      inputs: {
        baseBranch: { types: ["string"], required: true, description: "Base branch name" },
        remote: { types: ["string"], required: true, description: "Git remote name" },
        source: { types: ["string"], required: true, description: "Source branch name" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "targetBranch", description: "Base branch name" },
        { name: "strategy", description: "Merge strategy discriminator" },
        { name: "baseSha", description: "Tip of the base ref" },
        { name: "candidateHeadSha", description: "Tip of the source ref" },
        { name: "mergeBaseSha", description: "Merge base of source and base ref" },
        { name: "canMerge", description: "Whether the merge is ready" },
        { name: "conflictFiles", description: "Files with unresolved conflicts" },
        { name: "checkedAt", description: "ISO timestamp of the check" },
      ],
      errors: [{ code: "merge-not-ready", description: "Merge is not ready" }],
    },
    run: mergeReadyAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/push",
      description: "Push the worktree's source branch to a target branch",
      inputs: {
        source: { types: ["string"], required: true, description: "Source branch" },
        target: { types: ["string"], required: true, description: "Target branch" },
        remote: { types: ["string"], required: true, description: "Git remote name" },
        force: { types: ["boolean"], default: false, description: "Push with --force" },
        forceWithLease: { types: ["boolean"], default: false, description: "Push with --force-with-lease" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Push status discriminator" },
        { name: "source", description: "Source branch" },
        { name: "target", description: "Target branch" },
        { name: "remote", description: "Git remote name" },
        { name: "refspec", description: "Resolved refspec" },
        { name: "workDir", description: "Workspace directory" },
        { name: "landedCommit", description: "Tip commit that was pushed" },
        { name: "pushed", description: "Whether the push succeeded" },
        { name: "force", description: "Whether force mode was used" },
        { name: "forceWithLease", description: "Whether force-with-lease mode was used" },
        { name: "output", description: "Aggregated git push output" },
        { name: "steps", description: "Per-step git command results" },
      ],
      errors: [
        { code: "base-moved", description: "Target branch moved and the push is non-fast-forward" },
        { code: "push-failed", description: "Push failed for an unspecified reason" },
      ],
    },
    run: pushAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/create-github-pr",
      description: "Open or update a GitHub pull request for the current branch",
      inputs: {
        repositoryUrl: { types: ["string"], required: true, description: "Git repository URL used to select the GitHub repository" },
        source: { types: ["string"], required: true, description: "Source branch" },
        target: { types: ["string"], required: true, description: "Target branch" },
        draft: { types: ["boolean"], default: true, description: "Open the PR as a draft" },
        title: { types: ["string"], description: "Literal PR title" },
        message: { types: ["string"], description: "Alias of title" },
        titleFrom: { types: ["string"], default: "issue.title", description: "Issue field source for the PR title" },
        body: { types: ["string"], description: "Literal PR body" },
        bodyFrom: { types: ["string"], default: "issue.body", description: "Issue field source for the PR body" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "PR status discriminator" },
        { name: "source", description: "Source branch" },
        { name: "targetBranch", description: "Target branch" },
        { name: "branch", description: "Head branch name" },
        { name: "prNumber", description: "PR number" },
        { name: "prUrl", description: "PR URL" },
        { name: "operation", description: "Operation discriminator (created/updated/reused)" },
        { name: "draft", description: "Whether the PR is a draft" },
        { name: "output", description: "Aggregated gh output" },
        { name: "steps", description: "Per-step gh command results" },
      ],
      errors: [
        { code: "config-error", description: "GitHub configuration is missing or invalid" },
        { code: "protection-conflict", description: "Branch protection rejected the PR" },
        { code: "base-moved", description: "Base branch moved and the PR is out-of-date" },
        { code: "pr-state-conflict", description: "Existing PR is in a conflicting state" },
        { code: "retry-safe", description: "PR operation is safe to retry" },
        { code: "create-pr-failed", description: "Failed to create the PR" },
      ],
      capabilities: ["issue-fields"],
    },
    run: createGitHubPrAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/mark-github-pr-ready",
      description: "Mark a GitHub pull request ready for review",
      inputs: {
        repositoryUrl: { types: ["string"], required: true, description: "Git repository URL used to select the GitHub repository" },
        prNumber: { types: ["number"], required: true, description: "Pull request number" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Status discriminator" },
        { name: "prNumber", description: "Pull request number" },
        { name: "prUrl", description: "Pull request URL" },
        { name: "state", description: "Resulting PR state" },
        { name: "previousState", description: "Original PR state" },
        { name: "transitioned", description: "Whether the ready transition occurred" },
        { name: "output", description: "Aggregated gh output" },
        { name: "steps", description: "Per-step gh command results" },
      ],
      errors: [
        { code: "config-error", description: "GitHub configuration is missing or invalid" },
        { code: "protection-conflict", description: "Branch protection rejected the transition" },
        { code: "base-moved", description: "Base branch moved and the PR is out-of-date" },
        { code: "pr-state-conflict", description: "Existing PR is in a conflicting state" },
        { code: "retry-safe", description: "Operation is safe to retry" },
        { code: "mark-ready-failed", description: "Failed to mark the PR ready" },
      ],
    },
    run: markGitHubPrReadyAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/merge-github-pr",
      description: "Merge a GitHub pull request via squash",
      inputs: {
        repositoryUrl: { types: ["string"], required: true, description: "Git repository URL used to select the GitHub repository" },
        method: { types: ["string"], default: "squash", description: "Merge method (only 'squash' is supported)" },
        prNumber: { types: ["number"], required: true, description: "Pull request number" },
        subject: { types: ["string"], description: "Literal squash commit subject" },
        subjectFrom: { types: ["string"], default: "issue.title", description: "Issue field source for the squash subject" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Merge status discriminator" },
        { name: "prNumber", description: "Pull request number" },
        { name: "prUrl", description: "Pull request URL" },
        { name: "mergeCommitSha", description: "Squash merge commit sha" },
        { name: "method", description: "Merge method used" },
        { name: "output", description: "Aggregated gh output" },
        { name: "steps", description: "Per-step gh command results" },
      ],
      errors: [
        { code: "base-moved", description: "Base branch moved and the PR is out-of-date" },
        { code: "retry-safe", description: "Merge operation is safe to retry" },
        { code: "config-error", description: "GitHub configuration is missing or invalid" },
        { code: "protection-conflict", description: "Branch protection rejected the merge" },
        { code: "pr-state-conflict", description: "Existing PR is in a conflicting state" },
        { code: "pr-checks-unavailable", description: "PR checks state could not be retrieved" },
        { code: "pr-checks-failed", description: "Required PR checks did not pass" },
        { code: "merge-failed", description: "Failed to merge the PR" },
      ],
      capabilities: ["issue-fields"],
    },
    run: mergeGitHubPrAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/github-pr-checks",
      description: "Wait for every GitHub pull request check to pass",
      inputs: {
        repositoryUrl: { types: ["string"], required: true, description: "Git repository URL used to select the GitHub repository" },
        prNumber: { types: ["number"], required: true, description: "Pull request number" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Check status discriminator" },
        { name: "prNumber", description: "Pull request number" },
        { name: "pollIntervalMs", description: "Polling interval in milliseconds" },
        { name: "message", description: "Human-readable check result" },
        { name: "output", description: "Aggregated gh output" },
        { name: "steps", description: "Per-step gh command results" },
      ],
      errors: [
        { code: "config-error", description: "GitHub configuration is missing or invalid" },
        { code: "pr-checks-unavailable", description: "PR checks state could not be retrieved" },
        { code: "pr-checks-failed", description: "Required PR checks did not pass" },
        { code: "aborted", description: "Polling was cancelled" },
      ],
    },
    run: async (inputs, host) => githubPrChecksAction(inputs, host),
  }),
  defineAction({
    manifest: {
      name: "mohist/github-pr-status",
      description: "Verify a GitHub pull request is in the expected state",
      inputs: {
        repositoryUrl: { types: ["string"], required: true, description: "Git repository URL used to select the GitHub repository" },
        prNumber: { types: ["number"], required: true, description: "Pull request number" },
        expect: { types: ["string"], default: "open,ready", description: "Comma-separated expected states (open/ready/merged)" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Status discriminator" },
        { name: "prNumber", description: "Pull request number" },
        { name: "prUrl", description: "Pull request URL" },
        { name: "prState", description: "Pull request state" },
        { name: "isDraft", description: "Whether the PR is a draft" },
        { name: "expectations", description: "Expected state tokens" },
        { name: "missing", description: "Missing expected tokens" },
        { name: "output", description: "Aggregated gh output" },
        { name: "steps", description: "Per-step gh command results" },
      ],
      errors: [{ code: "pr-status-failed", description: "Pull request status check failed" }],
    },
    run: githubPrStatusAction,
  }),
  defineAction({
    manifest: {
      name: "mohist/workspace-prepare",
      description: "Reset the workspace to the expected branch and clean residual state",
      inputs: {
        expectedBranch: { types: ["string"], required: true, description: "Expected workspace branch" },
      },
      outputs: [
        { name: "kind", description: "Output kind discriminator" },
        { name: "status", description: "Status discriminator" },
        { name: "expectedBranch", description: "Expected branch name" },
        { name: "head", description: "Snapshot of HEAD after preparation" },
        { name: "residual", description: "Snapshot of residual state after preparation" },
        { name: "porcelain", description: "Porcelain status after preparation" },
        { name: "step", description: "Step that produced the snapshot when failure occurred" },
        { name: "workDir", description: "Workspace directory" },
      ],
      errors: [{ code: "workspace-setup", description: "Workspace preparation failed" }],
    },
    run: workspacePrepareAction,
  }),
]

export function builtInActionNames(): ReadonlyArray<string> {
  return builtInActions.map((definition) => definition.manifest.name)
}
