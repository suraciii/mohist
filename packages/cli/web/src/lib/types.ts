export enum Stage {
  Backlog = 'backlog',
  Plan = 'plan',
  Build = 'build',
  Check = 'check',
  Integrate = 'integrate',
  Done = 'done',
}

export const STAGE_ORDER: Stage[] = [
  Stage.Backlog,
  Stage.Plan,
  Stage.Build,
  Stage.Check,
  Stage.Integrate,
  Stage.Done,
]

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Interrupted = 'interrupted',
  Closed = 'closed',
  Completed = 'completed',
}

export interface ApprovalState {
  status: 'pending' | 'awaiting' | 'approved' | 'rejected' | 'error'
  stage?: string
  output?: Record<string, unknown>
  requestedAt: string
  respondedAt?: string
}

export interface Issue {
  id: string
  number: number
  title: string
  body?: string
  stage: Stage
  status: IssueStatus
  projectId: string
  labels: string[]
  createdAt: string
  updatedAt: string
  projectName?: string
  projectPath?: string
  comments?: Comment[]
  approvalState?: ApprovalState
  mergeState?: 'pending' | 'merging' | 'merged' | 'build-failed' | 'conflict' | 'rebasing' | 'resolving' | 'blocked' | null
  priority?: string | null
  model?: string | null
  stageModels?: Record<string, string> | null
  archivedAt?: string
  blockedReason?: string
  checkSuite?: CheckSuite | null
}

export interface Project {
  id: string
  name: string
  path: string
  createdAt: string
  updatedAt: string
}

export interface Comment {
  id: string
  issueId: string
  body: string
  createdAt: string
}

export interface Question {
  id: string
  issueId: string
  question: string
  answer?: string
  status: 'pending' | 'answered' | 'expired'
  createdAt: string
  answeredAt?: string
}

export interface ApiResponse<T = unknown> {
  success: boolean
  data?: T
  error?: string
}

export interface AgentProgress {
  stage: string
  roundType?: string
  roundIndex?: number
  taskProgress?: { completed: number; total: number } | null
  lastActivityAt: string
}

export interface ActiveAgentInfo {
  issueId: string
  issueNumber: number
  projectId: string
  progress?: AgentProgress
}

export interface AgentStatus {
  running: boolean
  issueId: string | null
  issueNumber: number | null
  activeAgents: ActiveAgentInfo[]
  maxConcurrentAgents: number
  queueDepth: number
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>
  recoverableIssues: Array<{ issueNumber: number; stage: string }>
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

export type IssueDiffResponse = ChangesAvailability & {
  base: string
  head: string
  summary: ChangesSummary
  files: DiffFile[]
}

export type IssueCommitsResponse = ChangesAvailability & {
  base: string
  head: string
  summary: ChangesSummary & { commits: number }
  commits: CommitEntry[]
}

export type CommitDiffResponse = ChangesAvailability & {
  hash: string
  diff: string
}

export type PlanRoundStartEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundLabel: string
  roundIndex: number
  acpSessionId?: string
  coderSessionId?: string
}

export type PlanSessionUpdateEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundIndex: number
  sessionUpdate: string
  data: unknown
  acpSessionId?: string
  coderSessionId?: string
}

export type PlanRoundCompleteEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundLabel?: string
  roundIndex: number
  duration: number
  verdict?: string
}

export type AgentDetailEventMap = {
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number }
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number }
  coder_text_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string; coderSessionId?: string; model?: string }
  coder_tool_call: { issueId: string; projectId: string; executionId: string; acpSessionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; coderSessionId?: string; model?: string }
  ralph_task_update: { issueId: string; projectId: string; executionId: string; taskId: string; taskIndex: number; totalTasks: number; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt?: number; error?: string }
  ralph_loop_progress: { issueId: string; projectId: string; executionId: string; completed: number; failed: number; total: number }
  plan_round_start: PlanRoundStartEvent
  plan_session_update: PlanSessionUpdateEvent
  plan_round_complete: PlanRoundCompleteEvent
  coder_recovery_status: { issueId: string; projectId: string; executionId: string; acpSessionId: string; status: 'detected' | 'recovering' | 'recovered' | 'failed'; attempt: number; reason?: string }
  coder_session_started: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; executionId?: string; model?: string; coderType?: string; stage?: string; taskDescription?: string; title?: string | null }
  coder_session_completed: { issueId: string; projectId: string; coderSessionId: string; status: 'completed' | 'failed'; duration: number }
  coder_session_failed: { issueId: string; projectId: string; coderSessionId: string; reason?: string }
  coder_session_cancelled: { issueId: string; projectId: string; coderSessionId: string; reason?: string }
  coder_session_status_changed: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; status: string; lastDataAt?: string | null; probeSentAt?: string | null; probeDeadlineAt?: string | null; failureReason?: string | null }
  agent_paused: { issueId: string; projectId: string }
  question_asked: { issueId: string; projectId: string; questionId: string; question: string }
  question_answered: { issueId: string; projectId: string; questionId: string; answer: string }
  check_update: { issueId: string; projectId: string; checkName: string; status: string; duration?: number; autoFixed?: boolean; verdict?: string; snapshotSha?: string }
  check_suite_status_changed: { issueId: string; projectId: string; issueNumber: number; suiteStatus: string; snapshotSha: string }
  stage_task_update: { issueId: string; projectId: string; stage: string; taskId: string; taskTitle: string; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt: number; artifacts: string[] }
}

export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string }
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string }
  agent_started: { issueId: string; projectId: string }
  agent_completed: { issueId: string; projectId: string }
  agent_paused: { issueId: string; projectId: string }
  agent_error: { issueId: string; projectId: string; error: string }
  agent_blocked: { issueId: string; projectId: string; issueNumber: number; blockedReason: string; retryCount: number }
  approval_requested: { issueId: string; projectId: string; stage: string }
  question_asked: { issueId: string; projectId: string; questionId: string; question: string }
  question_answered: { issueId: string; projectId: string; questionId: string; answer: string }
  explore_crystallized: { sessionId: string; issueId: string; projectId: string }
  merge_queued: { issueId: string; projectId: string; issueNumber: number; position: number }
  merge_started: { issueId: string; projectId: string; issueNumber: number }
  merge_completed: { issueId: string; projectId: string; issueNumber: number }
  merge_failed: { issueId: string; projectId: string; issueNumber: number; reason: string }
  rebase_started: { issueId: string; projectId: string; issueNumber: number }
  rebase_progress: { issueId: string; projectId: string; issueNumber: number; step: 'fetching' | 'checking' | 'rebasing' | 'verifying' }
  rebase_completed: { issueId: string; projectId: string; issueNumber: number; rebased: boolean }
  rebase_conflict: { issueId: string; projectId: string; issueNumber: number; conflicts: string[]; status?: string; error?: string }
  agent_conflict_resolution_started: { issueId: string; projectId: string; issueNumber: number }
  agent_conflict_resolution_completed: { issueId: string; projectId: string; issueNumber: number }
  agent_conflict_resolution_failed: { issueId: string; projectId: string; issueNumber: number; error: string }
  check_started: { issueId: string; projectId: string; issueNumber: number }
  check_update: { issueId: string; projectId: string; checkName: string; status: string; duration?: number; autoFixed?: boolean; verdict?: string; snapshotSha?: string }
  check_suite_status_changed: { issueId: string; projectId: string; issueNumber: number; suiteStatus: string; snapshotSha: string }
  integration_started: { issueId: string; projectId: string; issueNumber: number }
  integration_step_updated: { issueId: string; projectId: string; issueNumber: number; step: string; status: string; summary?: string; output?: unknown }
  integration_completed: { issueId: string; projectId: string; issueNumber: number; steps: Array<{ step: string; status: string; output?: unknown }> }
  integration_failed: { issueId: string; projectId: string; issueNumber: number; failingStep: string; error: string; output?: unknown }
} & AgentDetailEventMap

export type EventName = keyof EventMap

export interface DirEntry {
  name: string
  absolute: string
}

export interface ExploreSession {
  id: string
  projectId: string
  issueId: string | null
  issueNumber?: number
  title: string
  status: 'active' | 'crystallized' | 'archived'
  model?: string
  variant?: string
  createdAt: string
  updatedAt: string
}

export interface ExploreMessage {
  id: string
  sessionId: string
  role: 'user' | 'assistant'
  content: string
  toolCalls: ToolCallRecord[] | null
  createdAt: string
}

export interface ToolCallRecord {
  name: string
  args: Record<string, unknown>
  result: unknown
}

export interface ExploreSessionWithMessages {
  session: ExploreSession
  messages: ExploreMessage[]
}

export interface LogTailResult {
  file: string
  cursor: number
  lines: string[]
  truncated: boolean
  reset: boolean
}

export type ModelBadge = 'free' | 'latest'

export interface Model {
  id: string
  name: string
  badges: ModelBadge[]
  contextWindow: number
}

export interface ModelProvider {
  id: string
  name: string
  configured: boolean
  models: Model[]
}

export interface AgentSessionMessageItem {
  id: string
  role: string
  content: string | null
  toolCalls: string | null
  toolCallId: string | null
  toolName: string | null
  toolResult: string | null
  stepIndex: number
  createdAt: string
}

export interface WorkflowLogItem {
  id: string
  eventType: string
  data: unknown
  createdAt: string
}

export interface CoderSessionItem {
  id: string
  acpSessionId: string
  executionId: string | null
  taskDescription: string | null
  status: string
  createdAt: string
  completedAt: string | null
  model: string | null
  coderType: string | null
  stage: string | null
  title: string | null
  lastDataAt: string | null
  probeSentAt: string | null
  probeDeadlineAt: string | null
  failureReason: string | null
  workflowLogs: WorkflowLogItem[]
}

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing'

export interface FileChangeSummary {
  path: string
  operation: 'created' | 'modified' | 'deleted' | 'moved'
  additions?: number
  deletions?: number
  oldPath?: string
  rawDetail?: string
}

export interface TranscriptWarning {
  code: string
  message: string
}

export interface PromptSummary {
  title?: string
  subtitle?: string
  outputPath?: string
  contextFiles?: string[]
  kind: PromptKind
  rawText?: string
}

export interface SessionMetadata {
  sessionId: string
  coderSessionId: string
  issueId: string
  acpSessionId: string
  executionId: string | null
  title: string | null
  status: string
  statusKind?: SessionStatusKind
  model: string | null
  stage: string | null
  createdAt: string
  completedAt: string | null
  cwd?: string | null
  worktree?: string | null
  firstPromptSentAt?: string | null
  lastActivityAt?: string | null
  lastDataAt?: string | null
  probeSentAt?: string | null
  probeDeadlineAt?: string | null
  failureReason?: string | null
  eventCount?: number
  toolCount?: number
  turnCount?: number
  changedFiles?: FileChangeSummary[]
  warnings?: TranscriptWarning[]
  hasUnknownTools?: boolean
}

export type SessionStatusKind = 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'

export interface TextPart {
  id: string
  type: 'text'
  text: string
  startedAt: string
  completedAt: string | null
}

export interface ReasoningPart {
  id: string
  type: 'reasoning'
  text: string
  startedAt: string
  completedAt: string | null
}

export interface ToolPart {
  id: string
  type: 'tool'
  hidden?: boolean
  tool: {
    toolCallId: string
    normalizedName?: string
    displayTitle?: string
    displaySubtitle?: string
    category?: string
    toolName: string
    status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
    title?: string
    target?: string
    input?: string
    output?: string
    error?: string
    startedAt: string
    completedAt?: string | null
    rawInput?: string
    rawOutput?: string
    metadata?: Record<string, unknown>
    changedFiles?: FileChangeSummary[]
    warnings?: TranscriptWarning[]
  }
}

export interface ErrorPart {
  id: string
  type: 'error'
  message: string
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'
  at: string
}

export type SessionPart = TextPart | ReasoningPart | ToolPart | ErrorPart

export interface SessionTurn {
  id: string
  startedAt: string
  completedAt: string | null
  incomplete?: boolean
  user: {
    role: 'mohist'
    text: string
    kind: PromptKind
    sentAt: string
    summary?: PromptSummary
  }
  assistant: SessionPart[]
}

export interface CoderSessionDetail {
  id: string
  acpSessionId: string
  executionId: string | null
  taskDescription: string | null
  status: string
  createdAt: string
  completedAt: string | null
  model: string | null
  coderType: string | null
  stage: string | null
  title: string | null
  metadata: SessionMetadata
  turns: SessionTurn[]
  incomplete: boolean
  workflowLogs?: WorkflowLogItem[]
}

export interface ToolCallEntry {
  executionId: string
  toolName: string
  state: 'started' | 'completed' | 'failed' | 'pending' | 'running' | 'cancelled'
  args?: string
  result?: string
  error?: string
  duration?: number
  stepIndex?: number
  timestamp: number
  acpSessionId?: string
  toolCallId?: string
  rawInput?: string
  rawOutput?: string
  title?: string
  changedFiles?: FileChangeSummary[]
}

export interface TaskProgressEntry {
  taskId: string
  taskIndex: number
  totalTasks: number
  status: 'pending' | 'running' | 'passed' | 'failed' | 'retrying'
  executionId?: string
  attempt?: number
  error?: string
}

export type TaskProgressMap = Map<string, TaskProgressEntry>

export interface LoopProgress {
  completed: number
  failed: number
  total: number
}

export interface CoderTextBuffer {
  executionId: string
  acpSessionId: string
  text: string
}

export interface Task {
  id: string
  title: string
  description?: string
  acceptanceCriteria?: string[]
  dependsOn?: string[]
  passes: boolean
  attempts: number
  error?: string | null
  durations?: number[]
}

export interface BuildStatus {
  stage: string
  status: string
  progress: {
    completed: number
    failed: number
    total: number
    currentTask: string | null
  }
  tasks: Task[]
}

export interface RebaseConflictState {
  issueNumber: number
  conflicts: string[]
  status: string
  error?: string
}

export interface LiveTaskState {
  activeTaskId: string | null
  activeTaskElapsedMs: number | null
  rebaseConflict: RebaseConflictState | null
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

export interface GeneralConfig {
  agentTimeout: number
  maxConcurrentAgents: number
  pollInterval: number
}

export interface AgentRuntimeConfig {
  timeout: number
  stageTimeout: number
  taskTimeout: number
  maxConcurrent: number
  maxGracePeriods: number
  pollInterval: number
}

export interface AgentSessionInfo {
  issueNumber: number
  issueTitle: string
  issueStage: string
  sessionId: string
  status: string
  model: string | null
  taskDescription: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string | null
}

export interface SystemInfo {
  version: string
  gitHash: string
  sourceHead: string | null
  server: {
    host: string
    port: number
    status: 'running'
  }
  paths: {
    db: string
    config: string
    opencode: string | null
    logs: string
  }
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

export interface IntegrationHealthGatePolicy {
  policyName: string
  command: string
  timeout: number
  enabled: boolean
}

export interface CheckReadinessOutput {
  specImpact?: OpenSpecSyncOutput
  mergeReadiness?: MergeReadinessOutput
  healthGatePolicy?: IntegrationHealthGatePolicy
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

export interface DoneEvidenceOutput {
  reviewOutput?: Record<string, unknown>
  specSyncSummary?: OpenSpecSyncOutput
  archivePath?: string
  mergeTruth?: {
    targetBranch: string
    baseSha: string
    headSha: string
    mergedSha?: string
  }
  finalHealthResult?: {
    passed: boolean
    command: string
    duration: number
    summary: string
    exitCode?: number
    timedOut?: boolean
  }
}

export type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped'
export type StageCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error'
export type StageStateStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped'

export interface StageTaskCause {
  type: 'check-failure' | 'health-gate-failure' | 'retry' | 'rebase' | 'merge-conflict' | 'unknown';
  checkName?: string;
  taskId?: string;
  message?: string;
}

export interface StageTaskState {
  taskId: string
  title: string
  status: StageTaskStatus
  source: 'static' | 'dynamic'
  order: number
  attempts: number
  duration: number
  artifacts: string[]
  output: unknown
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
  reason?: string
  causedBy?: StageTaskCause
}

export interface StageCheckState {
  checkName: string
  status: StageCheckStatus
  message: string | null
  output: unknown
  runCount: number
  lastRunAt: string | null
  updatedAt: string
}

export interface StageApprovalState {
  status: string
  output: unknown
  requestedAt: string | null
  respondedAt: string | null
}

export interface StageStateRead {
  stage: Stage
  status: StageStateStatus
  tasks: StageTaskState[]
  checks: StageCheckState[]
  approval: StageApprovalState | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
}

export interface IssueStageStateResponse {
  issueId: string
  issueNumber: number
  stages: StageStateRead[]
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

export interface WorkflowTask {
  id: string
  taskId: string
  title: string
  status: WorkflowTaskStatus
  taskOrder: number
  attempts: number
  duration: number
  artifacts: string[]
  output: unknown
  reason: string | null
  causedBy: WorkflowTaskCause | null
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
}

export interface WorkflowStageRun {
  stage: Stage
  status: WorkflowStageRunStatus
  tasks: WorkflowTask[]
  checks: WorkflowCheck[]
  approvalStatus: string | null
  approvalOutput: unknown | null
  approvalRequestedAt: string | null
  approvalRespondedAt: string | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
}

export interface WorkflowRun {
  id: string
  issueId: string
  issueNumber: number
  status: WorkflowRunStatus
  currentStage: Stage
  stageRuns: WorkflowStageRun[]
}
