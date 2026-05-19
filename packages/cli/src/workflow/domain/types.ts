import { Stage } from '../../types';
import type { ResultContract, SelfRepairPolicy, WorkflowItem, WorkflowSnapshot } from '../../types/workflow-results';

export type WorkflowRunStatus = 'running' | 'passed' | 'failed' | 'cancelled';
export type StageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';
export type TaskRunStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
export type CheckRunStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error';
export type WorkItemAttemptState = 'running' | 'completed' | 'failed' | 'interrupted';
export type WorkflowRecoverySummary = 'running' | 'awaiting-approval' | 'waiting-for-recovery' | 'completed';
export type FailureReason =
  | 'task-failed'
  | 'check-unrepaired'
  | 'approval-rejected'
  | 'post-delivery-check-failed'
  | 'post-merge-health-failed'
  | 'work-interrupted';

export interface CausedByMetadata {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy';
  checkName?: string;
  taskId?: string;
  message?: string;
}

export interface FailureDetails {
  reason: FailureReason;
  stage: Stage;
  taskId?: string;
  checkName?: string;
  message?: string;
  causedBy?: CausedByMetadata;
}

export type AgentPromptSource =
  | { ref: string }
  | { file: string }
  | { inline: string };

export interface WorkItemAttempt {
  state: WorkItemAttemptState;
  attemptNumber: number;
  startedAt: string;
  completedAt: string | null;
  output: unknown | null;
  error: string | null;
  diagnostic: string | null;
  queueTaskId: string | null;
  acpSessionId: string | null;
  coderSessionId: string | null;
  executionId: string | null;
  processPid: number | null;
}

export interface TaskDefinition {
  id: string;
  title: string;
  source?: 'builtin' | 'project';
  uses?: string;
  with?: Record<string, unknown>;
  emits?: string[];
  dependsOn?: string[];
  resultContract?: ResultContract;
  selfRepairPolicy?: SelfRepairPolicy;
}

export interface CheckDefinition {
  name: string;
  title: string;
  source?: 'builtin' | 'project';
  uses?: string;
  with?: Record<string, unknown>;
  onFailure?: CheckFailureAction;
}

export interface CheckFailureRetry {
  limit: number;
  task: TaskDefinition;
  inputFrom?: ReactionInputSelector[];
}

export interface CheckFailureAction {
  retry?: CheckFailureRetry;
}

export interface CheckFailurePolicy {
  checkName: string;
  fixTaskId: string;
  fixTaskTitle: string;
  maxAttempts: number;
  inputFrom?: ReactionInputSelector[];
}

export type WorkSourceKind = 'static' | 'ralph' | 'runtime';

export type BuildWorkSourceState =
  | { evaluated: true; tasks: MaterializedTaskInput[] }
  | { evaluated: true; missing: true }
  | { evaluated: true; invalid: true }
  | { evaluated: true; empty: true }
  | { evaluated: false };

export interface WorkSourceDefinition {
  kind: WorkSourceKind;
  taskIds?: string[];
}

export type TaskExecutionKind = 'agent-session' | 'service-call' | 'ralph-task' | 'repair-task' | 'rebase-task';

export interface TaskExecutionPolicy {
  taskId: string;
  kind: TaskExecutionKind;
  workSourceKind?: WorkSourceKind;
  agentSessionRef?: string;
}

export type CheckPhase = 'pre-task' | 'post-task' | 'approval';

export interface CheckPolicy {
  checkName: string;
  phase: CheckPhase;
}

export interface ApprovalPolicy {
  checkName: string;
}

export type ReactionInputSelector =
  | { type: 'failed-check-output' }
  | { type: 'check-items'; filter?: 'blocking' | 'all' }
  | { type: 'task-output'; taskId: string }
  | { type: 'artifact'; path: string }
  | { type: 'snapshot' }
  | { type: 'prior-task-outputs' };

export interface RepairPolicy {
  checkName: string;
  fixTaskId: string;
  fixTaskTitle: string;
  maxAttempts: number;
  inputFrom?: ReactionInputSelector[];
}

export type InvalidationTrigger = 'check-completion' | 'task-completion' | 'branch-rebase';

export interface InvalidationEntry {
  trigger: InvalidationTrigger;
  triggerTaskId?: string;
  when?: {
    shaChanged?: boolean;
    checkName?: string;
    outputContains?: Record<string, unknown>;
  };
  reason?: string;
  invalidates: {
    tasks?: string[];
    checks?: string[];
    approval?: boolean;
  };
}

export interface InvalidationPolicy {
  entries: InvalidationEntry[];
}

export type StageResetTarget = 'checks-and-approval' | 'checks' | 'approval';

export interface StageEventPolicy {
  reset: StageResetTarget;
}

export type WorkflowTasksFromSource = 'mohist/ralph-tasks';

export interface StageDefinition {
  stage: Stage;
  tasks: TaskDefinition[];
  tasksFrom?: WorkflowTasksFromSource;
  checks: CheckDefinition[];
  on?: Record<string, StageEventPolicy>;
  requiresApproval?: boolean;
  approvalCheckName?: string;
}

export type CompiledStageDefinition = StageDefinition & {
  checkFailurePolicies?: CheckFailurePolicy[];
  workSources?: WorkSourceDefinition[];
  taskExecutionPolicies?: TaskExecutionPolicy[];
  checkPolicies: CheckPolicy[];
  approvalPolicy?: ApprovalPolicy;
  repairPolicies?: RepairPolicy[];
  invalidationPolicy?: InvalidationPolicy;
};

export interface WorkflowDefinition {
  id: string;
  name?: string;
  stages: StageDefinition[];
  defaults?: Record<string, unknown>;
}

export type WorkflowDefinitionSource =
  | { type: 'builtin'; id: string }
  | { type: 'project'; path: string }
  | { type: 'runtime'; id: string };

export interface WorkflowDefinitionSnapshot {
  workflowId: string;
  name?: string;
  source: WorkflowDefinitionSource;
  resolvedDefinition: WorkflowDefinition;
  compiledStageDefinitions: CompiledStageDefinition[];
  capturedAt: string;
}

export interface FailedCheckContext {
  checkName: string;
  verdict: 'PASS' | 'FAIL';
  blockingItems: WorkflowItem[];
  nonBlockingItems: WorkflowItem[];
  sourceArtifactRefs?: string[];
  snapshot?: WorkflowSnapshot;
  priorTaskOutputs?: Record<string, unknown>[];
}

export interface DeliveryMetadata {
  targetBranch?: string;
  baseSha?: string;
  candidateHeadSha?: string;
  landedSha?: string;
  rebased?: boolean;
}

export interface FreezePoint {
  taskId?: string;
  checkName?: string;
  delivery: DeliveryMetadata;
  frozenAt: string;
}

export interface TaskResultInput {
  status: 'completed' | 'failed' | 'skipped';
  attempts?: number;
  duration?: number;
  artifacts?: string[];
  output?: unknown;
  reason?: string;
  causedBy?: CausedByMetadata;
}

export interface CheckResultInput {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export interface ApprovalInput {
  output?: unknown;
}

export interface MaterializedTaskInput {
  id: string;
  title: string;
  order?: number;
  dependsOn?: string[];
}

export type WorkflowEvent =
  | { type: 'workflow-started'; stage: Stage }
  | { type: 'stage-started'; stage: Stage }
  | { type: 'stage-retried'; stage: Stage }
  | { type: 'task-completed'; stage: Stage; taskId: string }
  | { type: 'task-failed'; stage: Stage; taskId: string; reason: FailureDetails }
  | { type: 'task-invalidated'; stage: Stage; taskId: string; reason: string }
  | { type: 'check-invalidated'; stage: Stage; checkName: string; reason: string }
  | { type: 'check-recorded'; stage: Stage; checkName: string; status: CheckRunStatus }
  | { type: 'fix-task-scheduled'; stage: Stage; taskId: string; causedBy: CausedByMetadata }
  | { type: 'approval-requested'; stage: Stage }
  | { type: 'approval-approved'; stage: Stage }
  | { type: 'approval-rejected'; stage: Stage; reason: FailureDetails }
  | { type: 'evidence-stale-marked'; stage: Stage; reason: string }
  | { type: 'stage-completed'; stage: Stage }
  | { type: 'stage-failed'; stage: Stage; reason: FailureDetails }
  | { type: 'workflow-completed' }
  | { type: 'workflow-failed'; reason: FailureDetails }
  | { type: 'delivery-frozen'; stage: Stage; freezePoint: FreezePoint }
  | { type: 'integrate-frozen'; stage: Stage; freezePoint: FreezePoint };

export type WorkflowWork =
  | { kind: 'task'; stage: Stage; taskId: string }
  | { kind: 'check'; stage: Stage; checkName: string }
  | { kind: 'await-approval'; stage: Stage }
  | { kind: 'complete' }
  | { kind: 'blocked'; stage: Stage; reason: StageCompletionGuard }
  | { kind: 'failed'; reason: FailureDetails };

export type StageCompletionGuard =
  | { complete: true }
  | { complete: false; reason: 'missing-static-task'; taskId: string }
  | { complete: false; reason: 'missing-static-check'; checkName: string }
  | { complete: false; reason: 'static-task-not-successful'; taskId: string; status: TaskRunStatus }
  | { complete: false; reason: 'static-check-not-passed'; checkName: string }
  | { complete: false; reason: 'run-task-pending'; taskId: string }
  | { complete: false; reason: 'run-task-failed'; taskId: string }
  | { complete: false; reason: 'run-task-skipped'; taskId: string }
  | { complete: false; reason: 'dynamic-source-not-evaluated'; stage: Stage }
  | { complete: false; reason: 'dynamic-source-missing'; stage: Stage }
  | { complete: false; reason: 'dynamic-source-invalid'; stage: Stage }
  | { complete: false; reason: 'dynamic-source-empty'; stage: Stage }
  | { complete: false; reason: 'check-review-evidence-missing'; stage: Stage }
  | { complete: false; reason: 'check-review-evidence-stale'; stage: Stage }
  | { complete: false; reason: 'delivery-evidence-missing'; stage: Stage; taskId?: string; checkName?: string; uses?: string }
  | { complete: false; reason: 'approval-required'; stage: Stage };

export interface WorkflowDecision {
  events: WorkflowEvent[];
  nextWork: WorkflowWork;
}

export interface TaskRunSnapshot {
  id: string;
  title: string;
  status: TaskRunStatus;
  order: number;
  dependsOn: string[];
  attempts: number;
  duration: number;
  artifacts: string[];
  output: unknown | null;
  reason: string | null;
  causedBy: CausedByMetadata | null;
  latestAttempt: WorkItemAttempt | null;
}

export interface CheckStateSnapshot {
  name: string;
  title: string;
  status: CheckRunStatus;
  message: string | null;
  output: unknown | null;
  runCount: number;
  latestAttempt: WorkItemAttempt | null;
}

export interface VerificationEvidence {
  checkName: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  command: string;
  duration: number;
  summary: string;
  logExcerpt: string;
  checkedAt: string;
  candidateHeadSha?: string;
  baseSha?: string;
}

export interface ApprovalSnapshot {
  status: 'awaiting' | 'approved' | 'rejected';
  output: unknown | null;
  verificationEvidence?: VerificationEvidence | null;
  requestedAt: string;
  respondedAt: string | null;
  staleEvidenceDetected?: boolean;
}

export interface StageRunSnapshot {
  stage: Stage;
  status: StageRunStatus;
  order: number;
  attemptSequence?: number;
  tasks: TaskRunSnapshot[];
  checks: CheckStateSnapshot[];
  approval: ApprovalSnapshot | null;
  failure: FailureDetails | null;
  freezePoint: FreezePoint | null;
  buildWorkSourceState?: BuildWorkSourceState;
}

export interface WorkflowRunSnapshot {
  id: string;
  issueId: string;
  issueNumber: number;
  status: WorkflowRunStatus;
  currentStage: Stage;
  stageOrder: Stage[];
  workflowDefinitionSnapshot: WorkflowDefinitionSnapshot;
  stageRuns: StageRunSnapshot[];
  failure: FailureDetails | null;
}
