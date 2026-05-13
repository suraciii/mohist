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

export interface StageDefinition {
  stage: Stage;
  tasks: TaskDefinition[];
  checks: CheckDefinition[];
  requiresApproval?: boolean;
  approvalCheckName?: string;
  checkFailurePolicies?: CheckFailurePolicy[];
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
}

export type WorkflowEvent =
  | { type: 'workflow-started'; stage: Stage }
  | { type: 'stage-started'; stage: Stage }
  | { type: 'stage-retried'; stage: Stage }
  | { type: 'task-completed'; stage: Stage; taskId: string }
  | { type: 'task-failed'; stage: Stage; taskId: string; reason: FailureDetails }
  | { type: 'check-recorded'; stage: Stage; checkName: string; status: CheckRunStatus }
  | { type: 'fix-task-scheduled'; stage: Stage; taskId: string; causedBy: CausedByMetadata }
  | { type: 'approval-requested'; stage: Stage }
  | { type: 'approval-approved'; stage: Stage }
  | { type: 'approval-rejected'; stage: Stage; reason: FailureDetails }
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
  | { kind: 'failed'; reason: FailureDetails };

export interface WorkflowDecision {
  events: WorkflowEvent[];
  nextWork: WorkflowWork;
}

export interface TaskRunSnapshot {
  id: string;
  title: string;
  status: TaskRunStatus;
  order: number;
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

export interface ApprovalSnapshot {
  status: 'awaiting' | 'approved' | 'rejected';
  output: unknown | null;
  requestedAt: string;
  respondedAt: string | null;
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

  snapshot(): TaskRunSnapshot {
    return {
      id: this.id,
      title: this.title,
      status: this.status,
      order: this.order,
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

  constructor(
    readonly definition: StageDefinition,
    readonly order: number,
  ) {
    this.tasks = definition.tasks.map((task, index) => new TaskRun(task.id, task.title, index));
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
      if (this.tasks.some(existing => existing.id === task.id)) continue;
      this.tasks.push(new TaskRun(task.id, task.title, task.order ?? this.tasks.length));
    }
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  }

  nextTask(): TaskRun | null {
    return this.tasks.find(task => !task.terminal) ?? null;
  }

  nextCheck(): CheckState | null {
    if (!this.allRequiredTasksTerminal()) return null;
    return this.checks.find(check => check.status !== 'passed') ?? null;
  }

  allRequiredTasksTerminal(): boolean {
    return this.tasks.every(task => task.terminal);
  }

  allRequiredTasksSucceeded(): boolean {
    return this.tasks.every(task => task.status === 'completed' || task.status === 'skipped');
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

  appendAdHocTask(id: string, title: string, causedBy: CausedByMetadata): TaskRun {
    const task = new TaskRun(id, title, this.tasks.length);
    task.reason = causedBy.message ?? title;
    task.causedBy = causedBy;
    this.tasks.push(task);
    return task;
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

  requestApproval(now: string, output: unknown = null): void {
    this.status = 'awaiting-approval';
    this.approval = {
      status: 'awaiting',
      output,
      requestedAt: now,
      respondedAt: null,
    };
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
  },
  {
    stage: Stage.Check,
    tasks: [
      { id: 'ai-review', title: 'AI review' },
    ],
    checks: [
      { name: 'review-passed', title: 'Review passed' },
      { name: 'merge-ready', title: 'Merge ready' },
    ],
    requiresApproval: true,
    approvalCheckName: 'user-approval',
    checkFailurePolicies: [
      { checkName: 'review-passed', fixTaskId: 'fix-review-findings', fixTaskTitle: 'Fix review findings', maxAttempts: 1 },
      { checkName: 'merge-ready', fixTaskId: 'fix-merge-readiness', fixTaskTitle: 'Fix merge readiness', maxAttempts: 1 },
    ],
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

  materializeTasks(stage: Stage, tasks: MaterializedTaskInput[]): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    stageRun.materializeTasks(tasks);
    return this.decision([]);
  }

  completeTask(stage: Stage, taskId: string, result: TaskResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);

    const task = stageRun.findTask(taskId);
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

    if (result.status === 'failed') {
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
    if (stageRun.freezePoint) events.push({ type: 'integrate-frozen', stage, freezePoint: stageRun.freezePoint });
    return this.maybeCompleteStage(stageRun, events);
  }

  recordCheckResult(stage: Stage, result: CheckResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    if (!stageRun.allRequiredTasksTerminal()) throw new WorkflowDomainError(`Stage ${stage} cannot run checks before tasks are terminal`);
    if (!stageRun.allRequiredTasksSucceeded()) throw new WorkflowDomainError(`Stage ${stage} has failed tasks`);

    const check = stageRun.findCheck(result.name);
    const expected = stageRun.nextCheck();
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

    const policy = stageRun.definition.checkFailurePolicies?.find(candidate => candidate.checkName === result.name);
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

    for (const task of stageRun.tasks) {
      if (task.status === 'completed' || task.status === 'skipped') continue;
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

    return this.decision([{ type: 'stage-retried', stage }]);
  }

  nextWork(): WorkflowWork {
    if (this.status === 'passed') return { kind: 'complete' };
    if (this.status === 'failed') return { kind: 'failed', reason: this.failure! };
    const stageRun = this.currentStageRun();
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    const task = stageRun.nextTask();
    if (task) return { kind: 'task', stage: stageRun.stage, taskId: task.id };
    const check = stageRun.nextCheck();
    if (check) return { kind: 'check', stage: stageRun.stage, checkName: check.name };
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
    if (!stageRun.allRequiredTasksTerminal() || !stageRun.allRequiredTasksSucceeded()) return this.decision(events);
    if (!stageRun.allChecksPassed()) return this.decision(events);
    if (stageRun.definition.requiresApproval && stageRun.approval?.status !== 'approved') {
      if (!stageRun.approval) {
        stageRun.requestApproval(new Date().toISOString(), this.buildApprovalOutput(stageRun));
        events.push({ type: 'approval-requested', stage: stageRun.stage });
      }
      return this.decision(events);
    }
    return this.completeStage(stageRun, events);
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

  private buildApprovalOutput(stageRun: StageRun): unknown {
    if (stageRun.stage !== Stage.Check) return null;
    const reviewPassed = stageRun.checks.find(check => check.name === 'review-passed');
    const mergeReady = stageRun.checks.find(check => check.name === 'merge-ready');
    if (reviewPassed?.status !== 'passed' || mergeReady?.status !== 'passed') return null;
    if (!reviewPassed.output || typeof reviewPassed.output !== 'object') return null;
    const reviewOutput = reviewPassed.output as Record<string, unknown>;
    const snapshotSha = reviewOutput.snapshotSha;
    if (typeof snapshotSha !== 'string' || snapshotSha.length === 0) return null;
    return {
      result: reviewOutput.verdict,
      reviewReport: reviewOutput.reviewReport,
      dimensions: reviewOutput.dimensions,
      snapshotSha,
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

  private toCheckStatus(status: CheckResultInput['status']): CheckRunStatus {
    if (status === 'pass') return 'passed';
    if (status === 'fail') return 'failed';
    return status;
  }

  private extractDeliveryMetadata(output: unknown): DeliveryMetadata {
    if (!output || typeof output !== 'object') return {};
    const data = output as Record<string, unknown>;
    return {
      targetBranch: typeof data.targetBranch === 'string' ? data.targetBranch : undefined,
      baseSha: typeof data.baseSha === 'string' ? data.baseSha : undefined,
      candidateHeadSha: typeof data.candidateHeadSha === 'string' ? data.candidateHeadSha : undefined,
      landedSha: typeof data.landedSha === 'string' ? data.landedSha : undefined,
      rebased: typeof data.rebased === 'boolean' ? data.rebased : undefined,
    };
  }
}
