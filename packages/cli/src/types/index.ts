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
  Stage.Done
];

export const STAGE_TRANSITIONS: Record<Stage, Stage[]> = {
  [Stage.Backlog]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build],
  [Stage.Build]: [Stage.Check],
  [Stage.Check]: [Stage.Integrate, Stage.Build],
  [Stage.Integrate]: [Stage.Done, Stage.Build],
  [Stage.Done]: [],
};

export function isValidTransition(from: Stage, to: Stage): boolean {
  const allowed = STAGE_TRANSITIONS[from];
  return allowed?.includes(to) ?? false;
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Interrupted = 'interrupted',
  Closed = 'closed',
  Completed = 'completed'
}

export type Priority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4';

export const VALID_PRIORITIES: Priority[] = ['p0', 'p1', 'p2', 'p3', 'p4'];

export function normalizePriority(value: string | undefined | null): Priority | null {
  if (value === undefined || value === null || value === '') return null;
  const lower = value.toLowerCase();
  if (VALID_PRIORITIES.includes(lower as Priority)) {
    return lower as Priority;
  }
  return null;
}

export type ApprovalStatus = 'pending' | 'awaiting' | 'approved' | 'rejected' | 'error';

export enum MergeState {
  Pending = 'pending',
  Rebasing = 'rebasing',
  Merging = 'merging',
  Merged = 'merged',
  BuildFailed = 'build-failed',
  Conflict = 'conflict',
  Resolving = 'resolving',
  Blocked = 'blocked',
}

export interface ApprovalState {
  stage: Stage;
  status: ApprovalStatus;
  output: unknown;
  requestedAt: string;
  respondedAt?: string;
}

export interface Issue {
  id: string;
  number: number;
  title: string;
  body?: string;
  stage: Stage;
  status: IssueStatus;
  projectId: string;
  labels: string[];
  priority: Priority;
  createdAt: string;
  updatedAt: string;
  approvalState?: ApprovalState;
  mergeState?: MergeState;
  conflictRetryCount?: number;
  blockedReason?: string;
  retryCount?: number;
  model?: string;
  stageModels?: Record<string, string>;
  archivedAt?: string;
}

export interface Project {
  id: string;
  name: string;
  path: string;
  baseBranch: string;
  createdAt: string;
  updatedAt: string;
}

export interface Config {
  serverPort: number;
  serverHost: string;
  pollInterval: number;
  maxConcurrentAgents: number;
  agentTimeout: number;
  taskTimeout?: number;
  stageTimeout?: number;
  maxGracePeriods?: number;
}

export type CheckStatus = 'passed' | 'failed' | 'running' | 'pending';

export type CheckOverallResult = 'passed' | 'failed' | 'blocked';

export interface CheckResult {
  name: string;
  status: CheckStatus;
  duration?: number;
  autoFixed?: boolean;
  summary?: string;
  verdict?: string;
  dimensions?: string[];
  reviewReport?: string;
  buildLog?: string;
  conflictFiles?: string[];
}

export interface CheckSuiteOutput {
  checks: CheckResult[];
  overallResult: CheckOverallResult;
}

export type CheckSuiteStatus = 'running' | 'awaiting-approval' | 'passed' | 'failed';

export type CheckStateStatus = 'pending' | 'running' | 'passed' | 'failed';

export interface CheckState {
  status: CheckStateStatus;
  output?: unknown;
  ranAt?: string;
}

export interface CheckSuiteChecks {
  'review-passed': CheckState;
  'merge-ready': CheckState;
  'user-approval': CheckState;
}

export interface CheckSuite {
  id: string;
  issueId: string;
  snapshotSha: string;
  status: CheckSuiteStatus;
  checks: CheckSuiteChecks;
  createdAt: string;
  updatedAt: string;
}

export interface ServerState {
  isRunning: boolean;
  pid?: number;
  port: number;
  startedAt?: string;
}

export interface ApiResponse<T = any> {
  success: boolean;
  data?: T;
  error?: string;
  code?: string;
  details?: any;
}

export interface Comment {
  id: string;
  issueId: string;
  body: string;
  createdAt: string;
}

export type QuestionStatus = 'pending' | 'answered' | 'expired';

export interface Question {
  id: string;
  issueId: string;
  question: string;
  answer?: string;
  status: QuestionStatus;
  createdAt: string;
  answeredAt?: string;
}

export enum ExploreStatus {
  Active = 'active',
  Crystallized = 'crystallized',
  Archived = 'archived',
}

export interface ExploreSession {
  id: string;
  projectId: string;
  issueId: string | null;
  issueNumber?: number;
  title: string;
  status: ExploreStatus;
  model?: string;
  variant?: string;
  createdAt: string;
  updatedAt: string;
}

export interface ToolCallRecord {
  name: string;
  args: Record<string, unknown>;
  result: unknown;
}

export class ConfigConflictError extends Error {
  currentVersion: number;
  expectedVersion: number;

  constructor(currentVersion: number, expectedVersion: number) {
    super(`Config version conflict: expected ${expectedVersion} but current is ${currentVersion}`);
    this.name = 'ConfigConflictError';
    this.currentVersion = currentVersion;
    this.expectedVersion = expectedVersion;
  }
}

export type RebaseDecision = 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention';

export type DeferReason = 'agent-running' | 'task-running' | 'waiting-for-task-boundary' | 'rebase-already-pending';

export interface StaleEvidence {
  review: boolean;
  mergeReady: boolean;
  approval: boolean;
}

export interface BaseDriftState {
  drifted: boolean;
  baseBranch: string;
  observedBaseSha: string | null;
  currentBaseSha: string | null;
  candidateHeadSha: string | null;
  mergeBaseSha: string | null;
  decision: RebaseDecision;
  safeWindow: boolean;
  deferReason?: DeferReason;
  staleEvidence?: StaleEvidence;
  conflicts?: string[];
  message: string;
}

export interface ExploreMessage {
  id: string;
  sessionId: string;
  role: 'user' | 'assistant';
  content: string;
  toolCalls: ToolCallRecord[] | null;
  createdAt: string;
}

export interface IssueStartPrerequisite {
  issueId: string;
  prerequisiteIssueId: string;
  createdAt: string;
}

export enum EpicStatus {
  Active = 'active',
  Done = 'done',
  Closed = 'closed',
}

export type EpicPriority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4';

export interface Epic {
  id: string;
  projectId: string;
  title: string;
  description: string;
  priority: EpicPriority;
  status: EpicStatus;
  createdAt: string;
  updatedAt: string;
}

export interface EpicProgress {
  deliveredCount: number;
  totalIssueCount: number;
  blockedIssues: string[];
  activeIssues: string[];
  nextIssue: { id: string; number: number; title: string } | null;
  readyToMarkDone: boolean;
}

export interface EpicWithProgress extends Epic {
  progress: EpicProgress;
}

export interface LinkedIssue {
  id: string;
  number: number;
  title: string;
  status: IssueStatus;
  stage: Stage;
  priority: Priority;
}

export interface EpicDetail extends Epic {
  linkedIssues: LinkedIssue[];
  progress: EpicProgress;
}

export type {
  WorkflowVerdict,
  WorkflowItemSeverity,
  WorkflowItemStatus,
  WorkflowItem,
  WorkflowVerification,
  WorkflowSnapshot,
  StructuredWorkflowResult,
  ReactionTaskOutput,
  ResultContract,
  ResultOutputSource,
  SelfRepairPolicy,
  WorkflowConvergenceState,
  FailedCheckContext,
} from './workflow-results';
