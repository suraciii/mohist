export interface DiffFile {
  file: string
  additions: number
  deletions: number
  diff: string
  isBinary: boolean
}

export interface CommitEntry {
  hash: string
  shortHash: string
  message: string
  author: string
  date: string
  filesChanged: number
  additions: number
  deletions: number
  files: string[]
}

export interface CommitDiff {
  hash: string
  diff: string
}

export interface ComparisonMetadata {
  base: string
  head: string
  mergeBase: string
  ahead: number
  behind: number
  canFastForward: boolean
  comparison: 'merge-base'
}

export type ChangesUnavailableReason = 'workspace_removed' | 'branch_missing' | 'not_started' | 'git_error' | 'runner_unavailable'

export type ChangesAvailability =
  | { available: true; reason: null }
  | { available: false; reason: ChangesUnavailableReason; message: string }

export interface ChangesSummary {
  filesChanged: number
  commits: number
  additions: number
  deletions: number
}

export type IssueDiffResponse = ChangesAvailability & ComparisonMetadata & {
  summary: ChangesSummary
  files: DiffFile[]
}

export type IssueCommitsResponse = ChangesAvailability & ComparisonMetadata & {
  summary: ChangesSummary & { commits: number }
  commits: CommitEntry[]
}

export type CommitDiffResponse = ChangesAvailability & {
  hash: string
  diff: string
}
