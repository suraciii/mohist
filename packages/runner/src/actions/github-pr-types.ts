export type GitHubPrErrorCode =
  | "timeout"
  | "base-moved"
  | "retry-safe"
  | "config-error"
  | "protection-conflict"
  | "pr-state-conflict"
  | "pr-checks-unavailable"
  | "pr-checks-failed"

export interface GitHubPrStep {
  name: string
  command: string
  exitCode: number
  output: string
  status?: "timeout"
  timeoutMs?: number
}

export type GitHubPrStepMetadata = Pick<GitHubPrStep, "status" | "timeoutMs">

export function timeoutStepMetadata(result: { status?: "timeout"; timeoutMs?: number }): GitHubPrStepMetadata | undefined {
  if (result.status !== "timeout") return undefined
  return { status: "timeout", timeoutMs: result.timeoutMs }
}

export interface CreateGitHubPrOutput {
  kind: "create-github-pr"
  status: "completed" | "failed"
  source: string
  targetBranch: string
  branch: string | null
  prNumber: number | null
  prUrl: string | null
  operation: "created" | "updated" | "reused" | null
  draft: boolean
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
}

export interface MergeGitHubPrOutput {
  kind: "merge-github-pr"
  status: "completed" | "failed"
  prNumber: number | null
  prUrl: string | null
  mergeCommitSha: string | null
  method: "squash"
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
}

export interface MarkGitHubPrReadyOutput {
  kind: "mark-github-pr-ready"
  status: "completed" | "failed"
  prNumber: number | null
  prUrl: string | null
  state: "READY" | "DRAFT" | null
  previousState: "READY" | "DRAFT" | null
  transitioned: boolean
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
}
