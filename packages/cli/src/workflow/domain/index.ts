import { Stage } from '../../types';

export type WorkflowRunStatus = 'running' | 'passed' | 'failed' | 'cancelled';
export type StageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';
export type TaskRunStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
export type CheckRunStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error';
export type FailureReason = 'task-failed' | 'check-unrepaired' | 'approval-rejected' | 'post-merge-health-failed';

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

export interface TaskDefinition {
  id: string;
  title: string;
  dependsOn?: string[];
}

export interface CheckDefinition {
  name: string;
  title: string;
}

export interface CheckFailurePolicy {
  checkName: string;
  fixTaskId: string;
  fixTaskTitle: string;
  maxAttempts: number;
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
}

export type CheckPhase = 'pre-task' | 'post-task' | 'approval';

export interface CheckPolicy {
  checkName: string;
  phase: CheckPhase;
}

export interface ApprovalPolicy {
  checkName: string;
}

export interface RepairPolicy {
  checkName: string;
  fixTaskId: string;
  fixTaskTitle: string;
  maxAttempts: number;
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

export interface StageDefinition {
  stage: Stage;
  tasks: TaskDefinition[];
  checks: CheckDefinition[];
  requiresApproval?: boolean;
  approvalCheckName?: string;
  checkFailurePolicies?: CheckFailurePolicy[];
  workSources?: WorkSourceDefinition[];
  taskExecutionPolicies?: TaskExecutionPolicy[];
  checkPolicies?: CheckPolicy[];
  approvalPolicy?: ApprovalPolicy;
  repairPolicies?: RepairPolicy[];
  invalidationPolicy?: InvalidationPolicy;
}

export function getCheckFailurePolicy(
  stage: Stage,
  checkName: string,
  definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS,
): CheckFailurePolicy | null {
  return definitions
    .find(definition => definition.stage === stage)
    ?.checkFailurePolicies?.find(policy => policy.checkName === checkName) ?? null;
}

export interface DeliveryMetadata {
  targetBranch?: string;
  baseSha?: string;
  candidateHeadSha?: string;
  landedSha?: string;
  rebased?: boolean;
}

export interface FreezePoint {
  taskId: string;
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
  | { complete: false; reason: 'integrate-delivery-evidence-missing'; stage: Stage; taskId?: string; checkName?: string };

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
}

export interface CheckStateSnapshot {
  name: string;
  title: string;
  status: CheckRunStatus;
  message: string | null;
  output: unknown | null;
  runCount: number;
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
  stageRuns: StageRunSnapshot[];
  failure: FailureDetails | null;
}

export class WorkflowDomainError extends Error {}

export class TaskRun {
  status: TaskRunStatus = 'pending';
  dependsOn: string[] = [];
  attempts = 0;
  duration = 0;
  artifacts: string[] = [];
  output: unknown | null = null;
  reason: string | null = null;
  causedBy: CausedByMetadata | null = null;

  constructor(
    readonly id: string,
    readonly title: string,
    readonly order: number,
  ) {}

  get terminal(): boolean {
    return this.status === 'completed' || this.status === 'failed' || this.status === 'skipped';
  }

  get succeeded(): boolean {
    return this.status === 'completed';
  }

  snapshot(): TaskRunSnapshot {
    return {
      id: this.id,
      title: this.title,
      status: this.status,
      order: this.order,
      dependsOn: [...this.dependsOn],
      attempts: this.attempts,
      duration: this.duration,
      artifacts: [...this.artifacts],
      output: this.output,
      reason: this.reason,
      causedBy: this.causedBy,
    };
  }
}

export class CheckState {
  status: CheckRunStatus = 'pending';
  message: string | null = null;
  output: unknown | null = null;
  runCount = 0;

  constructor(
    readonly name: string,
    readonly title: string,
  ) {}

  snapshot(): CheckStateSnapshot {
    return {
      name: this.name,
      title: this.title,
      status: this.status,
      message: this.message,
      output: this.output,
      runCount: this.runCount,
    };
  }
}

export class StageRun {
  readonly tasks: TaskRun[];
  readonly checks: CheckState[];
  status: StageRunStatus = 'pending';
  approval: ApprovalSnapshot | null = null;
  failure: FailureDetails | null = null;
  freezePoint: FreezePoint | null = null;
  buildWorkSourceState: BuildWorkSourceState = { evaluated: false };

  constructor(
    readonly definition: StageDefinition,
    readonly order: number,
  ) {
    this.tasks = definition.tasks.map((task, index) => {
      const taskRun = new TaskRun(task.id, task.title, index);
      taskRun.dependsOn = [...(task.dependsOn ?? [])];
      return taskRun;
    });
    this.checks = definition.checks.map(check => new CheckState(check.name, check.title));
  }

  get stage(): Stage {
    return this.definition.stage;
  }

  start(): void {
    if (this.status !== 'pending') {
      throw new WorkflowDomainError(`Stage ${this.stage} cannot start from ${this.status}`);
    }
    this.status = 'running';
  }

  materializeTasks(tasks: MaterializedTaskInput[]): void {
    if (this.stage !== Stage.Build) {
      throw new WorkflowDomainError('Only the build stage can materialize tasks');
    }
    for (const task of [...tasks].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))) {
      const existing = this.tasks.find(candidate => candidate.id === task.id);
      if (existing) {
        existing.dependsOn = [...(task.dependsOn ?? existing.dependsOn)];
        continue;
      }
      const taskRun = new TaskRun(task.id, task.title, task.order ?? this.tasks.length);
      taskRun.dependsOn = [...(task.dependsOn ?? [])];
      this.tasks.push(taskRun);
    }
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  }

  nextTask(): TaskRun | null {
    return this.tasks.find(task => {
      if (task.terminal) return false;
      return task.dependsOn.every(depId => this.tasks.find(dep => dep.id === depId)?.succeeded);
    }) ?? null;
  }

  nextCheck(phase?: 'pre-task' | 'post-task'): CheckState | null {
    if (phase === 'post-task' && !this.allRequiredTasksTerminal()) return null;
    if (phase === undefined && !this.allRequiredTasksTerminal()) return null;
    for (const policy of this.nonApprovalCheckPolicies()) {
      if (phase && policy.phase !== phase) continue;
      const check = this.checks.find(candidate => candidate.name === policy.checkName);
      if (check && check.status !== 'passed') return check;
    }
    return null;
  }

  checkPhase(checkName: string): CheckPhase {
    return this.nonApprovalCheckPolicies().find(policy => policy.checkName === checkName)?.phase ?? 'post-task';
  }

  nonApprovalCheckPolicies(): CheckPolicy[] {
    if (this.definition.checkPolicies?.length) {
      return this.definition.checkPolicies.filter(policy => policy.phase !== 'approval');
    }
    return this.checks.map(check => ({ checkName: check.name, phase: 'post-task' as const }));
  }

  requiresApproval(): boolean {
    if (this.definition.requiresApproval === false) return false;
    return Boolean(this.definition.approvalPolicy ?? this.definition.requiresApproval);
  }

  allRequiredTasksTerminal(): boolean {
    return this.tasks.every(task => task.terminal);
  }

  allRequiredTasksSucceeded(): boolean {
    return this.tasks.every(task => task.status === 'completed');
  }

  hasFailedTask(): boolean {
    return this.tasks.some(task => task.status === 'failed' || task.status === 'skipped');
  }

  allChecksPassed(): boolean {
    return this.checks.every(check => check.status === 'passed');
  }

  findTask(taskId: string): TaskRun {
    const task = this.tasks.find(candidate => candidate.id === taskId);
    if (!task) throw new WorkflowDomainError(`Task ${taskId} does not exist in stage ${this.stage}`);
    return task;
  }

  findCheck(checkName: string): CheckState {
    const check = this.checks.find(candidate => candidate.name === checkName);
    if (!check) throw new WorkflowDomainError(`Check ${checkName} does not exist in stage ${this.stage}`);
    return check;
  }

  resetTask(taskId: string): void {
    const task = this.findTask(taskId);
    task.status = 'pending';
    task.attempts = 0;
    task.duration = 0;
    task.artifacts = [];
    task.output = null;
    task.reason = null;
    task.causedBy = null;
  }

  resetCheck(checkName: string): void {
    const check = this.findCheck(checkName);
    check.status = 'pending';
    check.message = null;
    check.output = null;
  }

  resetTaskAndDownstream(taskId: string): void {
    const task = this.findTask(taskId);
    const boundaryOrder = task.order;

    for (const t of this.tasks) {
      if (t.order >= boundaryOrder) {
        t.status = 'pending';
        t.duration = 0;
        t.artifacts = [];
        t.output = null;
        t.reason = null;
        t.causedBy = null;
      }
    }
  }

  resetCheckAndDownstream(checkName: string): void {
    const boundaryIndex = this.checks.findIndex(c => c.name === checkName);

    for (const [index, c] of this.checks.entries()) {
      if (index >= boundaryIndex) {
        c.status = 'pending';
        c.message = null;
        c.output = null;
      }
    }

    for (const t of this.tasks) {
      if (t.causedBy?.type === 'check-failure' && t.causedBy.checkName === checkName) {
        t.status = 'pending';
        t.duration = 0;
        t.artifacts = [];
        t.output = null;
        t.reason = null;
        t.causedBy = null;
      }
    }
  }

  scheduledFixCount(checkName: string): number {
    return this.tasks.filter(task => task.causedBy?.type === 'check-failure' && task.causedBy.checkName === checkName).length;
  }

  appendFixTask(policy: CheckFailurePolicy, causedBy: CausedByMetadata): TaskRun {
    const suffix = this.scheduledFixCount(policy.checkName) + 1;
    const id = this.tasks.some(task => task.id === policy.fixTaskId) ? `${policy.fixTaskId}:${suffix}` : policy.fixTaskId;
    const task = new TaskRun(id, policy.fixTaskTitle, this.tasks.length);
    task.reason = causedBy.message ?? `Repair ${policy.checkName}`;
    task.causedBy = causedBy;
    this.tasks.push(task);
    return task;
  }

  reopenForRepair(): void {
    this.status = 'running';
    this.failure = null;
    this.approval = null;
  }

  appendAdHocTask(id: string, title: string, causedBy: CausedByMetadata): TaskRun {
    const task = new TaskRun(id, title, this.tasks.length);
    task.reason = causedBy.message ?? title;
    task.causedBy = causedBy;
    this.tasks.push(task);
    return task;
  }

  removeGeneratedTasks(): void {
    const repairTaskIds = new Set([
      ...(this.definition.repairPolicies?.map(policy => policy.fixTaskId) ?? []),
      ...(this.definition.checkFailurePolicies?.map(policy => policy.fixTaskId) ?? []),
    ]);
    for (let index = this.tasks.length - 1; index >= 0; index--) {
      const task = this.tasks[index];
      const isRepairTask = [...repairTaskIds].some(fixTaskId => task.id === fixTaskId || task.id.startsWith(`${fixTaskId}:`));
      const isRuntimeTask = task.causedBy !== null || task.id === 'rebase-branch' || task.id === 'check:converge-review-snapshot';
      if (isRepairTask || isRuntimeTask) {
        this.tasks.splice(index, 1);
      }
    }
  }

  recordBuildWorkSourceEvaluated(tasks: MaterializedTaskInput[]): void {
    if (this.stage !== Stage.Build) return;
    this.buildWorkSourceState = { evaluated: true, tasks };
  }

  recordBuildWorkSourceMissing(): void {
    if (this.stage !== Stage.Build) return;
    if (this.buildWorkSourceState.evaluated) return;
    this.buildWorkSourceState = { evaluated: true, missing: true };
  }

  recordBuildWorkSourceInvalid(): void {
    if (this.stage !== Stage.Build) return;
    if (this.buildWorkSourceState.evaluated) return;
    this.buildWorkSourceState = { evaluated: true, invalid: true };
  }

  recordBuildWorkSourceEmpty(): void {
    if (this.stage !== Stage.Build) return;
    if (this.buildWorkSourceState.evaluated) return;
    this.buildWorkSourceState = { evaluated: true, empty: true };
  }

  materializeTaskForPersistence(id: string, title: string, order: number): TaskRun {
    const existing = this.tasks.find(task => task.id === id);
    if (existing) return existing;
    const task = new TaskRun(id, title, order);
    this.tasks.push(task);
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
    return task;
  }

  materializeCheckForPersistence(name: string, title: string): CheckState {
    const existing = this.checks.find(check => check.name === name);
    if (existing) return existing;
    const check = new CheckState(name, title);
    this.checks.push(check);
    return check;
  }

  requestApproval(now: string, output: unknown = null, verificationEvidence: VerificationEvidence | null = null): void {
    this.status = 'awaiting-approval';
    this.approval = {
      status: 'awaiting',
      output,
      verificationEvidence,
      requestedAt: now,
      respondedAt: null,
    };
  }

  markStaleEvidence(): void {
    if (this.approval) {
      this.approval.staleEvidenceDetected = true;
    }
  }

  snapshot(): StageRunSnapshot {
    return {
      stage: this.stage,
      status: this.status,
      order: this.order,
      tasks: this.tasks.map(task => task.snapshot()),
      checks: this.checks.map(check => check.snapshot()),
      approval: this.approval ? { ...this.approval } : null,
      failure: this.failure,
      freezePoint: this.freezePoint,
      buildWorkSourceState: this.stage === Stage.Build ? this.buildWorkSourceState : undefined,
    };
  }
}

export const DEFAULT_STAGE_DEFINITIONS: StageDefinition[] = [
  {
    stage: Stage.Plan,
    tasks: [
      { id: 'proposal', title: 'Generate proposal' },
      { id: 'specs', title: 'Write specs' },
      { id: 'design', title: 'Create design' },
      { id: 'tasks', title: 'Generate tasks' },
      { id: 'self-review', title: 'Self review' },
    ],
    checks: [
      { name: 'proposal-complete', title: 'Proposal complete' },
      { name: 'specs-complete', title: 'Specs complete' },
      { name: 'design-complete', title: 'Design complete' },
      { name: 'tasks-valid', title: 'Tasks valid' },
      { name: 'self-review-passed', title: 'Self review passed' },
      { name: 'health:plan', title: 'Plan health gate' },
    ],
    requiresApproval: true,
    approvalCheckName: 'user-approval',
    checkFailurePolicies: [
      { checkName: 'self-review-passed', fixTaskId: 'fix-plan-review', fixTaskTitle: 'Fix plan review findings', maxAttempts: 1 },
    ],
    workSources: [
      { kind: 'static', taskIds: ['proposal', 'specs', 'design', 'tasks', 'self-review'] },
    ],
    taskExecutionPolicies: [
      { taskId: 'proposal', kind: 'agent-session' },
      { taskId: 'specs', kind: 'agent-session' },
      { taskId: 'design', kind: 'agent-session' },
      { taskId: 'tasks', kind: 'agent-session' },
      { taskId: 'self-review', kind: 'agent-session' },
      { taskId: 'fix-plan-review', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
    ],
    checkPolicies: [
      { checkName: 'proposal-complete', phase: 'post-task' },
      { checkName: 'specs-complete', phase: 'post-task' },
      { checkName: 'design-complete', phase: 'post-task' },
      { checkName: 'tasks-valid', phase: 'post-task' },
      { checkName: 'self-review-passed', phase: 'post-task' },
      { checkName: 'health:plan', phase: 'post-task' },
    ],
    approvalPolicy: { checkName: 'user-approval' },
    repairPolicies: [
      { checkName: 'self-review-passed', fixTaskId: 'fix-plan-review', fixTaskTitle: 'Fix plan review findings', maxAttempts: 1 },
    ],
    invalidationPolicy: {
      entries: [],
    },
  },
  {
    stage: Stage.Build,
    tasks: [],
    checks: [
      { name: 'health:build', title: 'Build health gate' },
    ],
    checkFailurePolicies: [
      { checkName: 'health:build', fixTaskId: 'fix-build-health', fixTaskTitle: 'Fix build health', maxAttempts: 1 },
    ],
    workSources: [
      { kind: 'ralph' },
      { kind: 'runtime' },
    ],
    taskExecutionPolicies: [
      { taskId: 'fix-build-health', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      { taskId: '*', kind: 'ralph-task', workSourceKind: 'ralph' },
    ],
    checkPolicies: [
      { checkName: 'health:build', phase: 'post-task' },
    ],
    repairPolicies: [
      { checkName: 'health:build', fixTaskId: 'fix-build-health', fixTaskTitle: 'Fix build health', maxAttempts: 1 },
    ],
    invalidationPolicy: {
      entries: [],
    },
  },
  {
    stage: Stage.Check,
    tasks: [
      { id: 'ai-review', title: 'AI review' },
    ],
    checks: [
      { name: 'health:check', title: 'Check health gate' },
      { name: 'review-passed', title: 'Review passed' },
      { name: 'merge-ready', title: 'Merge ready' },
    ],
    requiresApproval: true,
    approvalCheckName: 'user-approval',
    checkFailurePolicies: [
      { checkName: 'health:check', fixTaskId: 'fix-check-health', fixTaskTitle: 'Fix check health', maxAttempts: 1 },
      { checkName: 'review-passed', fixTaskId: 'fix-review-findings', fixTaskTitle: 'Fix review findings', maxAttempts: 1 },
      { checkName: 'merge-ready', fixTaskId: 'fix-merge-readiness', fixTaskTitle: 'Fix merge readiness', maxAttempts: 1 },
    ],
    workSources: [
      { kind: 'static', taskIds: ['ai-review'] },
      { kind: 'runtime' },
    ],
    taskExecutionPolicies: [
      { taskId: 'ai-review', kind: 'agent-session' },
      { taskId: 'fix-check-health', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'fix-review-findings', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'fix-merge-readiness', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'check:converge-review-snapshot', kind: 'service-call', workSourceKind: 'runtime' },
      { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
    ],
    checkPolicies: [
      { checkName: 'health:check', phase: 'post-task' },
      { checkName: 'review-passed', phase: 'post-task' },
      { checkName: 'merge-ready', phase: 'post-task' },
    ],
    approvalPolicy: { checkName: 'user-approval' },
    repairPolicies: [
      { checkName: 'health:check', fixTaskId: 'fix-check-health', fixTaskTitle: 'Fix check health', maxAttempts: 1 },
      { checkName: 'review-passed', fixTaskId: 'fix-review-findings', fixTaskTitle: 'Fix review findings', maxAttempts: 1 },
      { checkName: 'merge-ready', fixTaskId: 'fix-merge-readiness', fixTaskTitle: 'Fix merge readiness', maxAttempts: 1 },
    ],
    invalidationPolicy: {
      entries: [
        {
          trigger: 'task-completion',
          triggerTaskId: 'fix-review-findings',
          reason: 'Review findings changed code; re-run AI review before rechecking',
          invalidates: {
            tasks: ['ai-review'],
            checks: ['health:check', 'review-passed', 'merge-ready'],
            approval: true,
          },
        },
        {
          trigger: 'task-completion',
          triggerTaskId: 'rebase-branch',
          when: { shaChanged: true },
          reason: 'Rebase changed the candidate snapshot; re-run review checks',
          invalidates: {
            tasks: ['ai-review'],
            checks: ['health:check', 'review-passed', 'merge-ready'],
            approval: true,
          },
        },
      ],
    },
  },
  {
    stage: Stage.Integrate,
    tasks: [
      { id: 'integrate:spec-sync', title: 'Sync specs' },
      { id: 'integrate:archive-change', title: 'Archive change' },
      { id: 'integrate:merge', title: 'Merge branch' },
    ],
    checks: [
      { name: 'health:integrate', title: 'Post-merge health check' },
    ],
    checkFailurePolicies: [
      { checkName: 'health:integrate', fixTaskId: 'fix-integrate-health', fixTaskTitle: 'Fix integrate health', maxAttempts: 1 },
    ],
    workSources: [
      { kind: 'static', taskIds: ['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge'] },
    ],
    taskExecutionPolicies: [
      { taskId: 'integrate:spec-sync', kind: 'service-call' },
      { taskId: 'integrate:archive-change', kind: 'service-call' },
      { taskId: 'integrate:merge', kind: 'service-call' },
      { taskId: 'fix-integrate-health', kind: 'repair-task', workSourceKind: 'runtime' },
      { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
    ],
    checkPolicies: [
      { checkName: 'health:integrate', phase: 'post-task' },
    ],
    repairPolicies: [],
    invalidationPolicy: {
      entries: [],
    },
  },
];

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'running';
  currentStage: Stage;
  failure: FailureDetails | null = null;

  private constructor(
    readonly id: string,
    readonly issueId: string,
    readonly issueNumber: number,
    readonly definitions: StageDefinition[],
  ) {
    if (definitions.length === 0) throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    this.stageRuns = definitions.map((definition, index) => new StageRun(definition, index));
    this.currentStage = definitions[0].stage;
  }

  static startWorkflow(input: {
    id: string;
    issueId: string;
    issueNumber: number;
    definitions?: StageDefinition[];
    now?: string;
  }): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = new WorkflowRun(input.id, input.issueId, input.issueNumber, input.definitions ?? DEFAULT_STAGE_DEFINITIONS);
    const firstStage = run.currentStageRun();
    firstStage.start();
    return {
      run,
      decision: run.decision([
        { type: 'workflow-started', stage: firstStage.stage },
        { type: 'stage-started', stage: firstStage.stage },
      ]),
    };
  }

  get stageOrder(): Stage[] {
    return this.definitions.map(definition => definition.stage);
  }

  currentStageRun(): StageRun {
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) throw new WorkflowDomainError(`Current stage ${this.currentStage} is not admitted by this workflow`);
    return stageRun;
  }

  stageRun(stage: Stage): StageRun {
    const stageRun = this.stageRuns.find(candidate => candidate.stage === stage);
    if (!stageRun) throw new WorkflowDomainError(`Stage ${stage} is not admitted by this workflow`);
    return stageRun;
  }

  materializeTasks(stage: Stage, tasks: MaterializedTaskInput[], buildWorkSourceState?: 'missing' | 'invalid' | 'empty'): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    stageRun.materializeTasks(tasks);
    if (stage === Stage.Build) {
      if (buildWorkSourceState === 'missing') {
        stageRun.recordBuildWorkSourceMissing();
      } else if (buildWorkSourceState === 'invalid') {
        stageRun.recordBuildWorkSourceInvalid();
      } else if (buildWorkSourceState === 'empty') {
        stageRun.recordBuildWorkSourceEmpty();
      } else if (tasks.length === 0) {
        stageRun.recordBuildWorkSourceEmpty();
      } else {
        stageRun.recordBuildWorkSourceEvaluated(tasks);
      }
    }
    return this.decision([]);
  }

  scheduleRebaseTask(reason?: string): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.currentStageRun();
    if (stageRun.stage !== this.currentStage) {
      throw new WorkflowDomainError(`Cannot schedule rebase in stage ${stageRun.stage}; current stage is ${this.currentStage}`);
    }

    const existingRebase = stageRun.tasks.find(t => t.id === 'rebase-branch' && !t.terminal);
    if (existingRebase) {
      return this.decision([]);
    }

    if (stageRun.status === 'awaiting-approval') {
      stageRun.status = 'running';
    }

    const causedBy: CausedByMetadata = {
      type: 'branch-changed',
      message: reason ?? 'Target branch moved; rebase requested',
    };
    stageRun.appendAdHocTask('rebase-branch', 'Rebase branch', causedBy);
    return this.decision([]);
  }

  completeTask(stage: Stage, taskId: string, result: TaskResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);

    const task = stageRun.findTask(taskId);
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) {
      throw new WorkflowDomainError(`Task ${taskId} cannot complete before earlier checks pass`);
    }
    const expected = stageRun.nextTask();
    if (!expected || expected.id !== task.id) {
      throw new WorkflowDomainError(`Task ${taskId} cannot complete before earlier tasks are terminal`);
    }

    task.status = result.status;
    task.attempts = result.attempts ?? task.attempts + 1;
    task.duration = result.duration ?? task.duration;
    task.artifacts = result.artifacts ?? task.artifacts;
    task.output = result.output ?? task.output;
    task.reason = result.reason ?? task.reason;
    task.causedBy = result.causedBy ?? task.causedBy;

    if (stage === Stage.Integrate && taskId === 'integrate:merge' && result.status === 'completed') {
      stageRun.freezePoint = {
        taskId,
        delivery: this.extractDeliveryMetadata(result.output),
        frozenAt: new Date().toISOString(),
      };
    }

    if (result.status === 'failed' || result.status === 'skipped') {
      const failure: FailureDetails = {
        reason: 'task-failed',
        stage,
        taskId,
        message: result.reason,
        causedBy: result.causedBy,
      };
      return this.fail(stageRun, failure, [
        { type: 'task-failed', stage, taskId, reason: failure },
      ]);
    }

    const events: WorkflowEvent[] = [{ type: 'task-completed', stage, taskId }];
    const invalidationEvents = this.applyTaskCompletionInvalidation(stageRun, taskId, result);
    events.push(...invalidationEvents);
    if (stage === Stage.Check && taskId === 'check:converge-review-snapshot' && result.status === 'completed') {
      events.push(...this.invalidateStaleCheckEvidenceAfterConvergence(stageRun, result));
    }
    if (stageRun.freezePoint) events.push({ type: 'integrate-frozen', stage, freezePoint: stageRun.freezePoint });
    return this.maybeCompleteStage(stageRun, events);
  }

  recordCheckResult(stage: Stage, result: CheckResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    const checkPhase = stageRun.checkPhase(result.name);
    if (checkPhase === 'approval') throw new WorkflowDomainError(`Check ${result.name} is an approval check`);
    if (checkPhase === 'post-task' && !stageRun.allRequiredTasksTerminal()) throw new WorkflowDomainError(`Stage ${stage} cannot run checks before tasks are terminal`);
    if (checkPhase === 'post-task' && !stageRun.allRequiredTasksSucceeded()) throw new WorkflowDomainError(`Stage ${stage} has failed tasks`);
    if (checkPhase === 'pre-task' && stageRun.hasFailedTask()) throw new WorkflowDomainError(`Stage ${stage} has failed tasks`);

    const check = stageRun.findCheck(result.name);
    const expected = stageRun.nextCheck(checkPhase);
    if (!expected || expected.name !== check.name) {
      throw new WorkflowDomainError(`Check ${result.name} cannot run before earlier checks pass`);
    }

    check.status = this.toCheckStatus(result.status);
    check.message = result.message ?? null;
    check.output = this.normalizeCheckOutput(stageRun, result) ?? null;
    if (result.status !== 'pending') check.runCount += 1;

    const events: WorkflowEvent[] = [{ type: 'check-recorded', stage, checkName: check.name, status: check.status }];
    if (this.needsCheckConvergenceTask(stageRun, result)) {
      const causedBy: CausedByMetadata = {
        type: 'system-policy',
        checkName: result.name,
        message: 'Converge review snapshot before approval',
      };
      const task = stageRun.appendAdHocTask('check:converge-review-snapshot', 'Converge review snapshot', causedBy);
      check.status = 'pending';
      events.push({ type: 'fix-task-scheduled', stage, taskId: task.id, causedBy });
      return this.decision(events);
    }
    if (result.status === 'pending' || result.status === 'pass') {
      return this.maybeCompleteStage(stageRun, events);
    }

    if (stage === Stage.Integrate && result.name === 'health:integrate' && stageRun.freezePoint) {
      return this.fail(stageRun, {
        reason: 'post-merge-health-failed',
        stage,
        checkName: result.name,
        message: result.message,
      }, events);
    }

    const policy =
      stageRun.definition.repairPolicies?.find(candidate => candidate.checkName === result.name) ??
      stageRun.definition.checkFailurePolicies?.find(candidate => candidate.checkName === result.name);
    const scheduledFixCount = stageRun.scheduledFixCount(result.name);
    if (policy && scheduledFixCount < policy.maxAttempts) {
      const causedBy: CausedByMetadata = {
        type: 'check-failure',
        checkName: result.name,
        message: result.message,
      };
      const fixTask = stageRun.appendFixTask(policy, causedBy);
      check.status = 'pending';
      events.push({ type: 'fix-task-scheduled', stage, taskId: fixTask.id, causedBy });
      return this.decision(events);
    }

    return this.fail(stageRun, {
      reason: 'check-unrepaired',
      stage,
      checkName: result.name,
      message: result.message,
      causedBy: { type: 'check-failure', checkName: result.name, message: result.message },
    }, events);
  }

  approveStage(stage: Stage, input: ApprovalInput = {}): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'awaiting-approval' || !stageRun.approval) {
      throw new WorkflowDomainError(`Stage ${stage} is not awaiting approval`);
    }
    if (stageRun.approval.staleEvidenceDetected) {
      throw new WorkflowDomainError(`Approval cannot be submitted: evidence is stale due to base drift or rebase. Please rebase or rerun checks before approving.`);
    }
    stageRun.approval = {
      ...stageRun.approval,
      status: 'approved',
      output: input.output ?? null,
      respondedAt: new Date().toISOString(),
    };
    return this.completeStage(stageRun, [{ type: 'approval-approved', stage }]);
  }

  rejectStage(stage: Stage, input: ApprovalInput = {}): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'awaiting-approval' || !stageRun.approval) {
      throw new WorkflowDomainError(`Stage ${stage} is not awaiting approval`);
    }
    stageRun.approval = {
      ...stageRun.approval,
      status: 'rejected',
      output: input.output ?? null,
      respondedAt: new Date().toISOString(),
    };
    const failure: FailureDetails = {
      reason: 'approval-rejected',
      stage,
      message: typeof input.output === 'string' ? input.output : undefined,
    };
    return this.fail(stageRun, failure, [{ type: 'approval-rejected', stage, reason: failure }]);
  }

  retryStage(stage: Stage): WorkflowDecision {
    if (this.status !== 'failed') {
      throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
    }
    const stageRun = this.stageRun(stage);
    if (this.currentStage !== stage) {
      throw new WorkflowDomainError(`Stage ${stage} is not current stage ${this.currentStage}`);
    }
    if (stageRun.status !== 'failed') {
      throw new WorkflowDomainError(`Stage ${stage} is not failed`);
    }

    const stageFailureReason = stageRun.failure?.reason;
    const runFailureReason = this.failure?.reason;

    this.status = 'running';
    this.failure = null;

    for (const priorStageRun of this.stageRuns) {
      if (priorStageRun.order >= stageRun.order) break;
      if (priorStageRun.status !== 'passed') continue;
      for (const task of priorStageRun.tasks) {
        if (task.status === 'completed') continue;
        task.status = 'completed';
        if (task.attempts === 0) task.attempts = 1;
        task.reason = null;
        task.causedBy = null;
      }
    }

    stageRun.status = 'running';
    const wasApprovalRejected = (stageFailureReason ?? runFailureReason) === 'approval-rejected';
    const failedTask = stageRun.tasks.find(t => t.status === 'failed' || t.status === 'skipped');
    const failedCheck = stageRun.checks.find(c => c.status === 'failed' || c.status === 'error');
    stageRun.failure = null;
    stageRun.approval = null;

    if (wasApprovalRejected) {
      for (const task of stageRun.tasks) {
        task.status = 'pending';
        task.duration = 0;
        task.artifacts = [];
        task.output = null;
        task.reason = null;
        task.causedBy = null;
      }
      for (const check of stageRun.checks) {
        check.status = 'pending';
        check.message = null;
        check.output = null;
      }
    } else {
      if (failedTask) {
        stageRun.resetTaskAndDownstream(failedTask.id);
        for (const check of stageRun.checks) {
          check.status = 'pending';
          check.message = null;
          check.output = null;
        }
      } else if (failedCheck) {
        if (
          stage === Stage.Check &&
          stageRun.tasks.some(task => task.id.startsWith('fix-review-findings') && task.status === 'completed')
        ) {
          const reason = 'Retrying Check after review findings were fixed; re-run AI review before rechecking';
          stageRun.resetTask('ai-review');
          for (const checkName of ['health:check', 'review-passed', 'merge-ready']) {
            stageRun.resetCheck(checkName);
          }
          return this.decision([
            { type: 'stage-retried', stage },
            { type: 'task-invalidated', stage, taskId: 'ai-review', reason },
            { type: 'check-invalidated', stage, checkName: 'health:check', reason },
            { type: 'check-invalidated', stage, checkName: 'review-passed', reason },
            { type: 'check-invalidated', stage, checkName: 'merge-ready', reason },
          ]);
        }

        for (const task of stageRun.tasks) {
          if (!task.terminal) {
            const isRepairTaskForFailedCheck = task.causedBy?.type === 'check-failure' && task.causedBy.checkName === failedCheck.name;
            if (!isRepairTaskForFailedCheck) {
              task.status = 'pending';
              task.duration = 0;
              task.artifacts = [];
              task.output = null;
              task.reason = null;
              task.causedBy = null;
            }
          }
        }
        stageRun.resetCheckAndDownstream(failedCheck.name);
      } else {
        for (const task of stageRun.tasks) {
          if (!task.terminal) {
            task.status = 'pending';
            task.duration = 0;
            task.artifacts = [];
            task.output = null;
            task.reason = null;
            task.causedBy = null;
          }
        }
        for (const check of stageRun.checks) {
          check.status = 'pending';
          check.message = null;
          check.output = null;
        }
      }
    }

    return this.decision([{ type: 'stage-retried', stage }]);
  }

  canRetryStage(stage: Stage): boolean {
    if (this.status !== 'failed') return false;
    if (this.currentStage !== stage) return false;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === stage);
    if (!stageRun) return false;
    if (stageRun.status !== 'failed') return false;
    return true;
  }

  rerunStage(stage: Stage): WorkflowDecision {
    const stageRun = this.assertCurrentStage(stage);

    this.status = 'running';
    this.failure = null;

    for (const priorStageRun of this.stageRuns) {
      if (priorStageRun.order >= stageRun.order) break;
      if (priorStageRun.status !== 'passed') continue;
      for (const task of priorStageRun.tasks) {
        if (task.status === 'completed') continue;
        task.status = 'completed';
        if (task.attempts === 0) task.attempts = 1;
        task.reason = null;
        task.causedBy = null;
      }
    }

    stageRun.status = 'running';
    stageRun.failure = null;
    stageRun.approval = null;

    stageRun.removeGeneratedTasks();

    for (const task of stageRun.tasks) {
      task.status = 'pending';
      task.duration = 0;
      task.artifacts = [];
      task.output = null;
      task.reason = null;
      task.causedBy = null;
      task.attempts = 0;
    }
    for (const check of stageRun.checks) {
      check.status = 'pending';
      check.message = null;
      check.output = null;
      check.runCount = 0;
    }

    return this.decision([{ type: 'stage-retried', stage }]);
  }

  nextWork(): WorkflowWork {
    if (this.status === 'passed') return { kind: 'complete' };
    if (this.status === 'failed') return { kind: 'failed', reason: this.failure! };
    const stageRun = this.currentStageRun();
    const failedTask = stageRun.tasks.find(task => task.status === 'failed' || task.status === 'skipped');
    if (failedTask) {
      const failure: FailureDetails = {
        reason: 'task-failed',
        stage: stageRun.stage,
        taskId: failedTask.id,
        message: failedTask.reason ?? undefined,
        causedBy: failedTask.causedBy ?? undefined,
      };
      this.fail(stageRun, failure, []);
      return { kind: 'failed', reason: failure };
    }
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) return { kind: 'check', stage: stageRun.stage, checkName: preTaskCheck.name };
    const task = stageRun.nextTask();
    if (task) return { kind: 'task', stage: stageRun.stage, taskId: task.id };
    const check = stageRun.nextCheck('post-task');
    if (check) return { kind: 'check', stage: stageRun.stage, checkName: check.name };
    const guard = this.evaluateStageCompletionGuard(stageRun);
    if (!guard.complete) return { kind: 'blocked', stage: stageRun.stage, reason: guard };
    return { kind: 'complete' };
  }

  snapshot(): WorkflowRunSnapshot {
    return {
      id: this.id,
      issueId: this.issueId,
      issueNumber: this.issueNumber,
      status: this.status,
      currentStage: this.currentStage,
      stageOrder: this.stageOrder,
      stageRuns: this.stageRuns.map(stageRun => stageRun.snapshot()),
      failure: this.failure,
    };
  }

  private assertRunning(): void {
    if (this.status !== 'running') throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
  }

  private assertCurrentStage(stage: Stage): StageRun {
    const stageRun = this.stageRun(stage);
    if (stageRun.stage !== this.currentStage) {
      throw new WorkflowDomainError(`Stage ${stage} is not current stage ${this.currentStage}`);
    }
    return stageRun;
  }

  private maybeCompleteStage(stageRun: StageRun, events: WorkflowEvent[]): WorkflowDecision {
    const guard = this.evaluateStageCompletionGuard(stageRun);
    if (!guard.complete) return this.decision(events);
    if (stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      if (!stageRun.approval) {
        const verificationEvidence = this.extractVerificationEvidence(stageRun);
        stageRun.requestApproval(new Date().toISOString(), this.buildApprovalOutput(stageRun, verificationEvidence), verificationEvidence);
        events.push({ type: 'approval-requested', stage: stageRun.stage });
      } else if (stageRun.approval.status === 'awaiting') {
        stageRun.status = 'awaiting-approval';
      }
      return this.decision(events);
    }
    return this.completeStage(stageRun, events);
  }

  private evaluateStageCompletionGuard(stageRun: StageRun): StageCompletionGuard {
    for (const taskDef of stageRun.definition.tasks) {
      const taskRun = stageRun.tasks.find(t => t.id === taskDef.id);
      if (!taskRun) return { complete: false, reason: 'missing-static-task', taskId: taskDef.id };
      if (taskRun.status !== 'completed') return { complete: false, reason: 'static-task-not-successful', taskId: taskDef.id, status: taskRun.status };
    }

    for (const checkDef of stageRun.definition.checks) {
      const checkRun = stageRun.checks.find(c => c.name === checkDef.name);
      if (!checkRun) return { complete: false, reason: 'missing-static-check', checkName: checkDef.name };
      if (checkRun.status !== 'passed') return { complete: false, reason: 'static-check-not-passed', checkName: checkDef.name };
    }

    if (stageRun.stage === Stage.Build) {
      const state: BuildWorkSourceState = stageRun.buildWorkSourceState;
      if (!state.evaluated) return { complete: false, reason: 'dynamic-source-not-evaluated', stage: Stage.Build };
      if ('missing' in state && state.missing) return { complete: false, reason: 'dynamic-source-missing', stage: Stage.Build };
      if ('invalid' in state && state.invalid) return { complete: false, reason: 'dynamic-source-invalid', stage: Stage.Build };
      if ('empty' in state && state.empty) return { complete: false, reason: 'dynamic-source-empty', stage: Stage.Build };
    }

    if (stageRun.stage === Stage.Check) {
      const checkEvidenceGuard = this.evaluateCheckReviewEvidenceGuard(stageRun);
      if (!checkEvidenceGuard.complete) return checkEvidenceGuard;
    }

    if (stageRun.stage === Stage.Integrate) {
      const integrateEvidenceGuard = this.evaluateIntegrateDeliveryEvidenceGuard(stageRun);
      if (!integrateEvidenceGuard.complete) return integrateEvidenceGuard;
    }

    for (const taskRun of stageRun.tasks) {
      if (!taskRun.terminal) return { complete: false, reason: 'run-task-pending', taskId: taskRun.id };
    }

    return { complete: true };
  }

  private evaluateIntegrateDeliveryEvidenceGuard(stageRun: StageRun): StageCompletionGuard {
    const specSync = stageRun.tasks.find(task => task.id === 'integrate:spec-sync');
    const archive = stageRun.tasks.find(task => task.id === 'integrate:archive-change');
    const merge = stageRun.tasks.find(task => task.id === 'integrate:merge');
    const health = stageRun.checks.find(check => check.name === 'health:integrate');
    if (specSync?.status !== 'completed') {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };
    }
    if (archive?.status !== 'completed' || !archive.output || typeof archive.output !== 'object') {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:archive-change' };
    }
    const archiveOutput = archive.output as Record<string, unknown>;
    if (typeof archiveOutput.archivePath !== 'string' || archiveOutput.archivePath.length === 0) {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:archive-change' };
    }
    if (merge?.status !== 'completed') {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:merge' };
    }
    const delivery = stageRun.freezePoint?.delivery ?? {};
    if (!delivery.landedSha && !delivery.targetBranch) {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:merge' };
    }
    if (health?.status !== 'passed') {
      return { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, checkName: 'health:integrate' };
    }
    return { complete: true };
  }

  private evaluateCheckReviewEvidenceGuard(stageRun: StageRun): StageCompletionGuard {
    const aiReview = stageRun.tasks.find(task => task.id === 'ai-review');
    const healthCheck = stageRun.checks.find(check => check.name === 'health:check');
    const reviewPassed = stageRun.checks.find(check => check.name === 'review-passed');
    const mergeReady = stageRun.checks.find(check => check.name === 'merge-ready');
    if (aiReview?.status !== 'completed') return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    if (healthCheck?.status !== 'passed') return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    if (reviewPassed?.status !== 'passed' || !reviewPassed.output || typeof reviewPassed.output !== 'object') {
      return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    }
    if (mergeReady?.status !== 'passed' || !mergeReady.output || typeof mergeReady.output !== 'object') {
      return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    }

    const reviewOutput = reviewPassed.output as Record<string, unknown>;
    const mergeReadyOutput = mergeReady.output as Record<string, unknown>;
    const reviewSnapshotSha = reviewOutput.snapshotSha;
    const mergeCandidateHeadSha = mergeReadyOutput.candidateHeadSha;
    if (typeof reviewSnapshotSha !== 'string' || reviewSnapshotSha.length === 0) {
      return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    }
    if (typeof mergeCandidateHeadSha !== 'string' || mergeCandidateHeadSha.length === 0) {
      return { complete: false, reason: 'check-review-evidence-missing', stage: Stage.Check };
    }
    if (reviewSnapshotSha !== mergeCandidateHeadSha) {
      return { complete: false, reason: 'check-review-evidence-stale', stage: Stage.Check };
    }
    return { complete: true };
  }

  private completeStage(stageRun: StageRun, events: WorkflowEvent[]): WorkflowDecision {
    stageRun.status = 'passed';
    events.push({ type: 'stage-completed', stage: stageRun.stage });

    const next = this.stageRuns[stageRun.order + 1];
    if (!next) {
      this.status = 'passed';
      events.push({ type: 'workflow-completed' });
      return this.decision(events);
    }

    if (next.status !== 'pending') throw new WorkflowDomainError(`Next stage ${next.stage} is not pending`);
    this.currentStage = next.stage;
    next.start();
    events.push({ type: 'stage-started', stage: next.stage });
    return this.decision(events);
  }

  private fail(stageRun: StageRun, failure: FailureDetails, events: WorkflowEvent[]): WorkflowDecision {
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
    events.push({ type: 'stage-failed', stage: stageRun.stage, reason: failure });
    events.push({ type: 'workflow-failed', reason: failure });
    return this.decision(events);
  }

  private decision(events: WorkflowEvent[]): WorkflowDecision {
    return { events, nextWork: this.nextWork() };
  }

  private buildApprovalOutput(stageRun: StageRun, verificationEvidence: VerificationEvidence | null): unknown {
    const approvalCheckName = stageRun.definition.approvalPolicy?.checkName ?? stageRun.definition.approvalCheckName;
    if (!approvalCheckName) return null;

    if (stageRun.stage === Stage.Plan) {
      const selfReview = stageRun.checks.find(check => check.name === 'self-review-passed');
      if (selfReview?.status !== 'passed') return null;
      if (!selfReview.output || typeof selfReview.output !== 'object') return null;
      const selfReviewOutput = selfReview.output as Record<string, unknown>;
      return {
        result: selfReviewOutput.verdict,
        selfReviewNotes: selfReviewOutput.selfReviewNotes,
        dimensions: selfReviewOutput.dimensions,
      };
    }

    if (stageRun.stage !== Stage.Check) return null;
    const reviewPassed = stageRun.checks.find(check => check.name === 'review-passed');
    const mergeReady = stageRun.checks.find(check => check.name === 'merge-ready');
    if (reviewPassed?.status !== 'passed' || mergeReady?.status !== 'passed') return null;
    if (!reviewPassed.output || typeof reviewPassed.output !== 'object') return null;
    if (!mergeReady.output || typeof mergeReady.output !== 'object') return null;
    const reviewOutput = reviewPassed.output as Record<string, unknown>;
    const mergeReadySnapshot = mergeReady.output as Record<string, unknown>;
    const snapshotSha = reviewOutput.snapshotSha;
    if (typeof snapshotSha !== 'string' || snapshotSha.length === 0) return null;

    const healthCheck = stageRun.checks.find(check => check.name === 'health:check');
    if (!healthCheck || healthCheck.status !== 'passed') {
      return {
        error: 'Cannot request check approval: health:check has not passed',
      };
    }
    const healthCheckOutput = healthCheck.output as Record<string, unknown> | undefined;
    if (healthCheckOutput?.enabled === false) {
      return { error: 'Cannot request check approval: health:check is disabled by policy and cannot serve as approval evidence' };
    }

    return {
      result: reviewOutput.verdict,
      reviewReport: reviewOutput.reviewReport,
      dimensions: reviewOutput.dimensions,
      snapshotSha,
      mergeReadySnapshot,
      verificationEvidence,
    };
  }

  private extractVerificationEvidence(stageRun: StageRun): VerificationEvidence | null {
    if (stageRun.stage !== Stage.Check) return null;
    const healthCheck = stageRun.checks.find(check => check.name === 'health:check');
    if (!healthCheck || healthCheck.status !== 'passed' || !healthCheck.output) return null;
    const output = healthCheck.output as Record<string, unknown>;
    return {
      checkName: healthCheck.name,
      status: healthCheck.status === 'passed' ? 'pass' : healthCheck.status === 'failed' ? 'fail' : healthCheck.status,
      command: (output.command as string) ?? '',
      duration: (output.duration as number) ?? 0,
      summary: (output.summary as string) ?? (output.message as string) ?? '',
      logExcerpt: (output.logExcerpt as string) ?? '',
      checkedAt: healthCheck.runCount > 0 ? new Date().toISOString() : '',
      candidateHeadSha: (output.candidateHeadSha as string) ?? undefined,
      baseSha: (output.baseSha as string) ?? undefined,
    };
  }

  private normalizeCheckOutput(stageRun: StageRun, result: CheckResultInput): unknown {
    if (stageRun.stage !== Stage.Check || result.name !== 'review-passed' || result.status !== 'pass') return result.output;
    if (!result.output || typeof result.output !== 'object') return result.output;
    const reviewOutput = result.output as Record<string, unknown>;
    if (typeof reviewOutput.snapshotSha === 'string' && reviewOutput.snapshotSha.length > 0) return result.output;
    const convergenceTask = stageRun.tasks.find(task => task.id === 'check:converge-review-snapshot');
    const convergenceOutput = convergenceTask?.output as { snapshotSha?: unknown } | null;
    if (typeof convergenceOutput?.snapshotSha !== 'string' || convergenceOutput.snapshotSha.length === 0) return result.output;
    return { ...reviewOutput, snapshotSha: convergenceOutput.snapshotSha };
  }

  private needsCheckConvergenceTask(stageRun: StageRun, result: CheckResultInput): boolean {
    if (stageRun.stage !== Stage.Check || result.name !== 'review-passed' || result.status !== 'pass') return false;
    if (!result.output || typeof result.output !== 'object') return false;
    const output = result.output as Record<string, unknown>;
    if (typeof output.snapshotSha === 'string' && output.snapshotSha.length > 0) return false;
    return !stageRun.tasks.some(task => task.id === 'check:converge-review-snapshot');
  }

  private invalidateStaleCheckEvidenceAfterConvergence(stageRun: StageRun, result: TaskResultInput): WorkflowEvent[] {
    const data = this.unwrapTaskOutput(result.output);
    const snapshotSha = typeof data?.snapshotSha === 'string' ? data.snapshotSha : null;
    if (!snapshotSha) return [];

    const events: WorkflowEvent[] = [];
    for (const checkName of ['health:check', 'merge-ready']) {
      const check = stageRun.checks.find(candidate => candidate.name === checkName);
      if (!check || check.status === 'pending') continue;
      const output = check.output as Record<string, unknown> | null;
      if (output?.candidateHeadSha === snapshotSha) continue;
      stageRun.resetCheck(checkName);
      events.push({
        type: 'check-invalidated',
        stage: stageRun.stage,
        checkName,
        reason: 'Review convergence changed candidate snapshot; re-run verification evidence',
      });
    }
    return events;
  }

  private toCheckStatus(status: CheckResultInput['status']): CheckRunStatus {
    if (status === 'pass') return 'passed';
    if (status === 'fail') return 'failed';
    return status;
  }

  private detectShaChanged(output: unknown): boolean {
    const data = this.unwrapTaskOutput(output);
    if (!data) return false;
    if (data.shaChanged === true) return true;
    const beforeBaseSha = typeof data.beforeBaseSha === 'string' ? data.beforeBaseSha : null;
    const afterBaseSha = typeof data.afterBaseSha === 'string' ? data.afterBaseSha : null;
    const beforeHeadSha = typeof data.beforeHeadSha === 'string' ? data.beforeHeadSha : null;
    const afterHeadSha = typeof data.afterHeadSha === 'string' ? data.afterHeadSha : null;
    if (!beforeBaseSha || !afterBaseSha || !beforeHeadSha || !afterHeadSha) return false;
    return beforeBaseSha !== afterBaseSha || beforeHeadSha !== afterHeadSha;
  }

  private evaluateInvalidationCondition(when: InvalidationEntry['when'] | undefined, output: unknown): boolean {
    if (!when) return true;
    if (when.shaChanged !== undefined) {
      const shaChanged = this.detectShaChanged(output);
      if (shaChanged !== when.shaChanged) return false;
    }
    if (when.checkName !== undefined) return true;
    if (when.outputContains !== undefined) {
      const data = this.unwrapTaskOutput(output);
      if (!data) return false;
      for (const [key, value] of Object.entries(when.outputContains)) {
        if (data[key] !== value) return false;
      }
    }
    return true;
  }

  private applyTaskCompletionInvalidation(stageRun: StageRun, taskId: string, result: TaskResultInput): WorkflowEvent[] {
    const events: WorkflowEvent[] = [];
    const policy = stageRun.definition.invalidationPolicy;
    if (!policy) return events;

    for (const entry of policy.entries) {
      if (entry.trigger !== 'task-completion') continue;
      if (entry.triggerTaskId && entry.triggerTaskId !== taskId) continue;
      if (!this.evaluateInvalidationCondition(entry.when, result.output)) continue;

      if (entry.invalidates.tasks) {
        for (const t of entry.invalidates.tasks) {
          try {
            stageRun.resetTask(t);
            const reason = entry.reason ?? `Policy invalidation after ${taskId}`;
            events.push({ type: 'task-invalidated', stage: stageRun.stage, taskId: t, reason });
          } catch {
            // task not in stage, skip
          }
        }
      }
      if (entry.invalidates.checks) {
        for (const c of entry.invalidates.checks) {
          try {
            stageRun.resetCheck(c);
            const reason = entry.reason ?? `Policy invalidation after ${taskId}`;
            events.push({ type: 'check-invalidated', stage: stageRun.stage, checkName: c, reason });
          } catch {
            // check not in stage, skip
          }
        }
      }
      if (entry.invalidates.approval && stageRun.approval) {
        stageRun.approval = null;
        if (stageRun.status === 'awaiting-approval') {
          stageRun.status = 'running';
        }
      }
    }
    return events;
  }

  private extractDeliveryMetadata(output: unknown): DeliveryMetadata {
    const data = this.unwrapTaskOutput(output);
    if (!data) return {};
    return {
      targetBranch: typeof data.targetBranch === 'string' ? data.targetBranch : undefined,
      baseSha: typeof data.baseSha === 'string' ? data.baseSha : undefined,
      candidateHeadSha: typeof data.candidateHeadSha === 'string' ? data.candidateHeadSha : undefined,
      landedSha: typeof data.landedSha === 'string' ? data.landedSha : undefined,
      rebased: typeof data.rebased === 'boolean' ? data.rebased : undefined,
    };
  }

  private unwrapTaskOutput(output: unknown): Record<string, unknown> | null {
    if (!output || typeof output !== 'object') return null;
    const data = output as Record<string, unknown>;
    if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
      return data.result as Record<string, unknown>;
    }
    return data;
  }
}
