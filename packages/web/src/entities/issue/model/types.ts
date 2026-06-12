export enum IssueStatus {
  Backlog = 'backlog',
  InProgress = 'in_progress',
  Done = 'done',
  Cancelled = 'cancelled',
}

export enum WorkflowStage {
  Plan = 'plan',
  Build = 'build',
  Check = 'check',
  Integrate = 'integrate',
  Done = 'done',
}

export const STATUS_ORDER: IssueStatus[] = [
  IssueStatus.Backlog,
  IssueStatus.InProgress,
  IssueStatus.Done,
  IssueStatus.Cancelled,
]

export const WORKFLOW_STAGE_ORDER: WorkflowStage[] = [
  WorkflowStage.Plan,
  WorkflowStage.Build,
  WorkflowStage.Check,
  WorkflowStage.Integrate,
  WorkflowStage.Done,
]

export enum IssueHealth {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Interrupted = 'interrupted',
  Cancelled = 'cancelled',
  Done = 'done',
}

export interface Comment {
  id: string
  issueId: string
  body: string
  createdAt: string
}

export interface ApprovalState {
  status: 'pending' | 'awaiting' | 'approved' | 'rejected' | 'error'
  stage?: string
  output?: Record<string, unknown>
  requestedAt: string
  respondedAt?: string
}

export interface IssuePrerequisiteSummary {
  issueId: string
  number: number
  title: string
  completed: boolean
  status: IssueStatus
  health: IssueHealth
}

export interface IssueStartEligibility {
  startable: boolean
  reason: 'ready' | 'not-startable-lifecycle' | 'waiting-for-completion'
  message?: string
  waitingForCompletion: IssuePrerequisiteSummary[]
}

export type WorkItemAttemptState = 'running' | 'completed' | 'failed' | 'interrupted'
export type WorkflowRecoverySummary = 'running' | 'awaiting-approval' | 'waiting-for-recovery' | 'completed'

export interface RecoveryProjection {
  currentWorkItem: {
    type: 'task' | 'check'
    id: string
    title: string
  } | null
  latestAttemptState: WorkItemAttemptState | null
  workflowSummaryState: WorkflowRecoverySummary | null
  allowedActions: string[]
}

export interface WorkflowConvergenceState {
  failedCheck?: string
  blockingItemCount: number
  directlyRepairedCount: number
  reactionAttempts: number
  attemptedItemIds: string[]
  resolvedItemIds: string[]
  unresolvedItemIds: string[]
  newBlockingItemIds: string[]
  nonBlockingItemIds: string[]
  blockedReason?: string
}

export type RebaseDecision = 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention'
export type DeferReason = 'agent-running' | 'task-running' | 'waiting-for-task-boundary' | 'rebase-already-pending'

export interface BaseDriftInfo {
  drifted: boolean
  decision: RebaseDecision | null
  safeWindow: boolean | null
  deferReason: DeferReason | null
  observedBaseSha: string | null
  currentBaseSha: string | null
  candidateHeadSha: string | null
  mergeBaseSha: string | null
  conflicts: string[] | null
  nextAction: string | null
}

export interface WorkflowStageProgress {
  stage: string
  total: number
  completed: number
  running: number
  failed: number
  currentTaskTitle?: string | null
}

export interface Issue {
  id: string
  number: number
  title: string
  body?: string
  status: IssueStatus
  workflowStage?: WorkflowStage | null
  workflowStatus?: string | null
  workflowStageProgress?: WorkflowStageProgress | null
  workflowRunId?: string | null
  workflowProfileId?: string | null
  health: IssueHealth
  projectId: string
  labels: string[]
  createdAt: string
  updatedAt: string
  projectName?: string
  projectPath?: string
  repository?: { name: string; path?: string; remote?: string; baseBranch: string } | null
  comments?: Comment[]
  approvalState?: ApprovalState
  priority?: string | null
  model?: string | null
  agentConfig?: Record<string, unknown> | null
  stageModels?: Record<string, string> | null
  archivedAt?: string
  blockedReason?: string
  checkSuite?: CheckSuite | null
  prerequisites?: IssuePrerequisiteSummary[]
  startEligibility?: IssueStartEligibility
  drift?: BaseDriftInfo | null
  primaryEpic?: { id: string; number: number | null; title: string; status: string; priority: string } | null
  recovery?: RecoveryProjection | null
  convergence?: WorkflowConvergenceState | null
}

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

export type ChangesUnavailableReason = 'worktree_removed' | 'branch_missing' | 'not_started' | 'git_error'

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

export interface RebaseConflictState {
  issueNumber: number
  conflicts: string[]
  status: string
  error?: string
}

export type ApprovalArtifact = {
  type: string
  path: string
  content: string
}

export type ApprovalOutput = {
  summary?: string
  artifacts?: ApprovalArtifact[]
  [key: string]: unknown
}

export interface CheckResult {
  name: string
  status: 'pending' | 'running' | 'pass' | 'fail' | 'error'
  duration?: number
  summary?: string
  message?: string
  buildLog?: string
  reviewReport?: string
  autoFixed?: boolean
  verdict?: string
  output?: unknown
}

export interface CheckSuiteOutput {
  checks: CheckResult[]
  overallResult: 'passed' | 'failed' | 'blocked'
}

export type CheckSuiteStatus = 'running' | 'awaiting-approval' | 'pass' | 'fail'
export type CheckStateStatus = 'pending' | 'running' | 'pass' | 'fail'

export interface CheckState {
  status: CheckStateStatus
  output?: unknown
  ranAt?: string
}

export interface CheckSuiteChecks {
  'review-passed': CheckState
  'merge-ready': CheckState
  'user-approval': CheckState
}

export interface CheckSuite {
  id: string
  issueId: string
  snapshotSha: string
  status: CheckSuiteStatus
  checks: CheckSuiteChecks
  createdAt: string
  updatedAt: string
}

export interface StageTaskResult {
  taskId: string
  title: string
  status: 'completed' | 'failed' | 'skipped'
  artifacts: string[]
  output?: unknown
  attempts: number
  duration: number
}

export type StageExecutionStatus = 'running' | 'awaiting-approval' | 'passed' | 'failed'

export interface StageExecution {
  id: string
  issueId: string
  stage: string
  status: StageExecutionStatus
  taskResults: StageTaskResult[]
  checkResults: CheckResult[]
  createdAt: string
  updatedAt: string
}

export interface IntegrationStepResult {
  step: string
  status: 'completed' | 'failed'
  output?: unknown
  startedAt: string
  completedAt: string
  duration: number
}

export interface OpenSpecSyncConflict {
  capability: string
  type: string
  detail: string
  requirementHeader?: string
}

export interface OpenSpecSyncCounts {
  added: number
  modified: number
  removed: number
  renamed: number
}

export interface OpenSpecSyncOutput {
  capabilities: string[]
  targetFiles: string[]
  counts: OpenSpecSyncCounts
  conflicts: OpenSpecSyncConflict[]
  valid: boolean
  errors?: string[]
}

export interface MergeReadinessOutput {
  targetBranch: string
  canFastForward: boolean
  cleanRebaseFeasible: boolean
  conflictFiles?: string[]
}

export interface IntegrationHealthCheckPolicy {
  policyName: string
  command: string
  timeout: number
  enabled: boolean
}

export interface CheckReadinessOutput {
  specImpact?: OpenSpecSyncOutput
  mergeReadiness?: MergeReadinessOutput
  healthCheckPolicy?: IntegrationHealthCheckPolicy
}

export interface IntegrationFailureOutput {
  failingStep: string
  capability?: string
  conflictedFiles?: string[]
  requirementHeader?: string
  mergeReason?: string
  healthCommand?: string
  healthSummary?: string
  healthLogExcerpt?: string
  nextAction: string
}

export type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped'
export type StageCheckStatus = 'pending' | 'running' | 'completed' | 'passed' | 'failed' | 'error'
export type StageStateStatus = 'pending' | 'running' | 'awaiting-approval' | 'completed' | 'passed' | 'failed' | 'skipped'
export type CheckRepairStatus = 'not-needed' | 'available' | 'pending' | 'running' | 'completed' | 'exhausted'

export interface CheckRepairState {
  checkName: string
  fixTaskId: string
  status: CheckRepairStatus
  attemptsUsed: number
  attemptsMax: number
  attemptsRemaining: number
  repairAvailable: boolean
  lastRepairTask: StageTaskState | null
  lastRepairStatus: StageTaskStatus | null
  followUpReviewStatus: StageCheckStatus | null
  stopReason: string | null
  unresolvedSummary: string | null
}

export interface StageTaskCause {
  type: 'check-failure' | 'health-check-failure' | 'retry' | 'rebase' | 'merge-conflict' | 'unknown'
  checkName?: string
  taskId?: string
  message?: string
}

export interface WorkItemOrigin {
  source: 'builtin' | 'project' | 'runtime'
  uses: string
}

export interface StageTaskState {
  taskId: string
  title: string
  status: StageTaskStatus
  sessionName?: string | null
  source?: 'static' | 'dynamic'
  origin?: WorkItemOrigin | null
  order: number
  attempts: number
  duration: number
  artifacts: string[]
  artifactSummaries?: WorkflowArtifactSummary[]
  output: unknown
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
  reason?: string
  causedBy?: StageTaskCause
  requiredFiles?: WorkflowTaskRequiredFile[]
  classification?: 'UserFacing' | 'Orchestration'
}

export interface StageCheckState {
  checkName: string
  title?: string
  status: StageCheckStatus
  message: string | null
  output: unknown
  runCount: number
  lastRunAt: string | null
  origin?: WorkItemOrigin | null
  updatedAt: string
}

export interface StageApprovalState {
  status: string
  output: unknown
  requestedAt: string | null
  respondedAt: string | null
}

export interface StageStateRead {
  stage: WorkflowStage
  status: StageStateStatus
  tasks: StageTaskState[]
  checks: StageCheckState[]
  approval: StageApprovalState | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
  failure?: WorkflowFailureDetails | null
  checkRepair?: CheckRepairState
}

export interface WorkflowTimeline {
  workflowRunId: string
  status: string
  currentStage: string | null
  pendingWork: WorkflowTimelinePendingWork | null
  stages: WorkflowTimelineStage[]
  availableActions: WorkflowTimelineAction[]
}

export interface WorkflowTimelinePendingWork {
  workId: string
  workType: string
  stage: string | null
  title: string | null
  uses: string | null
}

export interface WorkflowTimelineStage {
  stage: WorkflowStage
  status: StageStateStatus
  order: number
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  tasks: WorkflowTimelineTask[]
  checks: WorkflowTimelineCheck[]
  approval: StageApprovalState | null
}

export interface WorkflowTaskRequiredFile {
  path: string
  source: string
  canFetchContent: boolean
  markers?: string[]
}

export interface WorkflowArtifactSummary {
  artifactId: string
  path: string
  kind: 'file' | 'directory'
  displayName?: string | null
  size?: number | null
  recordedAt: string
}

export interface WorkflowArtifact {
  artifactId: string
  workflowRunId: string
  taskRunId: string
  path: string
  kind: 'file' | 'directory'
  contentType?: string | null
  size?: number | null
  recordedAt: string
  displayName?: string | null
}

export interface WorkflowArtifactDirectoryEntry {
  relativePath: string
  size: number
  contentType?: string | null
}

export interface WorkflowArtifactDirectory extends Omit<WorkflowArtifact, 'kind'> {
  kind: 'directory'
  entries?: WorkflowArtifactDirectoryEntry[]
  totalSize?: number
}

export interface WorkflowTimelineTask {
  id: string
  title: string
  uses: string | null
  status: StageTaskStatus
  sessionName?: string | null
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  attempts: number
  message: string | null
  requiredFiles?: WorkflowTaskRequiredFile[]
  classification?: 'UserFacing' | 'Orchestration'
  artifactSummaries?: WorkflowArtifactSummary[]
}

export interface WorkflowTimelineCheck {
  name: string
  title: string
  uses: string | null
  status: string
  message: string | null
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
}

export interface WorkflowTimelineAction {
  name: string
  label: string
  target: string | null
}

export interface IssueStageStateResponse {
  issueId: string
  issueNumber: number
  stages: StageStateRead[]
  drift?: BaseDriftInfo | null
}

export type WorkflowRunStatus = 'running' | 'passed' | 'failed' | 'cancelled'
export type WorkflowTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped'
export type WorkflowCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error'
export type WorkflowStageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped'

export interface WorkflowTaskCause {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy'
  checkName?: string
  taskId?: string
  message?: string
}

export interface WorkflowTaskResetCause {
  type: 'workflow-policy'
  taskId?: string
  eventName?: string
  message?: string
}

export interface WorkflowFailureDetails {
  reason: string
  stage: WorkflowStage
  taskId?: string
  checkName?: string
  message?: string | null
  causedBy?: WorkflowTaskCause | null
}

export interface WorkflowTask {
  id: string
  taskId: string
  title: string
  status: WorkflowTaskStatus
  origin?: WorkItemOrigin | null
  taskOrder: number
  attempts: number
  duration: number
  artifacts: string[]
  output: unknown
  reason: string | null
  causedBy: WorkflowTaskCause | null
  resetBy: WorkflowTaskResetCause | null
  startedAt: string | null
  completedAt: string | null
}

export interface WorkflowCheck {
  checkName: string
  title: string
  status: WorkflowCheckStatus
  message: string | null
  output: unknown
  runCount: number
  lastRunAt: string | null
  origin?: WorkItemOrigin | null
}

export interface WorkflowStageRun {
  stage: WorkflowStage
  status: WorkflowStageRunStatus
  definition?: WorkflowStageDefinition | null
  tasks: WorkflowTask[]
  checks: WorkflowCheck[]
  approvalStatus: string | null
  approvalOutput: unknown | null
  approvalRequestedAt: string | null
  approvalRespondedAt: string | null
  approval?: StageApprovalState | null
  failure?: WorkflowFailureDetails | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
  updatedAt?: string
}

export type WorkflowDefinitionSource =
  | { type: 'builtin'; id: string }
  | { type: 'project'; path: string }
  | { type: 'runtime'; id: string }

export interface WorkflowDefinitionMetadata {
  workflowId: string
  name?: string
  source: WorkflowDefinitionSource
  capturedAt: string
  stageOrder: WorkflowStage[]
  stageDefinitions?: WorkflowStageDefinition[]
}

export interface WorkflowCheckFailurePolicy {
  checkName: string
  fixTaskId: string
  fixTaskTitle: string
  maxAttempts: number
}

export interface WorkflowStageDefinition {
  stage: WorkflowStage
  checkFailurePolicies?: WorkflowCheckFailurePolicy[]
}

export interface WorkflowRun {
  id: string
  issueId: string
  issueNumber: number
  status: WorkflowRunStatus
  currentStage: WorkflowStage
  workflowDefinition?: WorkflowDefinitionMetadata | null
  stageRuns: WorkflowStageRun[]
  failure?: WorkflowFailureDetails | null
}

export interface IssueWorkflowProfileYamlResponse {
  issueNumber: number
  projectId: string
  issueKey: string
  sourceTemplateId?: string | null
  hasCustomTemplate: boolean
  yaml: string | null
  workflowRunId: string | null
  profileId: string
  updateMode: string
  variables: unknown
  updatedAt: string
  templateSource?: 'system' | 'project' | 'custom'
}
