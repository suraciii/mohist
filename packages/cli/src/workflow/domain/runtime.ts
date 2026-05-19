import { Stage } from '../../types';
import { DEFAULT_STAGE_DEFINITIONS, createDefaultWorkflowDefinitionSnapshot } from './default-workflow';
import { WorkflowDomainError } from './errors';
import { cloneWorkflowDefinitionSnapshot, createWorkflowDefinitionSnapshot } from './workflow-definition';
import { getWorkflowUseDefinition, inferWorkflowTaskUse, validateWorkflowUseEvidence } from '../uses-catalog';
import type {
  ApprovalInput,
  ApprovalSnapshot,
  BuildWorkSourceState,
  CausedByMetadata,
  CheckFailurePolicy,
  CheckPhase,
  CheckPolicy,
  CheckResultInput,
  CheckRunStatus,
  CheckStateSnapshot,
  DeliveryMetadata,
  FailureDetails,
  FreezePoint,
  InvalidationEntry,
  MaterializedTaskInput,
  StageCompletionGuard,
  StageDefinition,
  StageRunSnapshot,
  StageRunStatus,
  TaskResultInput,
  TaskRunSnapshot,
  TaskRunStatus,
  VerificationEvidence,
  WorkItemAttempt,
  WorkflowDecision,
  WorkflowDefinitionSnapshot,
  WorkflowEvent,
  WorkflowRecoverySummary,
  WorkflowRunSnapshot,
  WorkflowRunStatus,
  WorkflowWork,
} from './types';

export function getCheckFailurePolicy(
  stage: Stage,
  checkName: string,
  definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS,
): CheckFailurePolicy | null {
  return definitions
    .find(definition => definition.stage === stage)
    ?.checkFailurePolicies?.find(policy => policy.checkName === checkName) ?? null;
}

export class TaskRun {
  status: TaskRunStatus = 'pending';
  dependsOn: string[] = [];
  attempts = 0;
  duration = 0;
  artifacts: string[] = [];
  output: unknown | null = null;
  reason: string | null = null;
  causedBy: CausedByMetadata | null = null;
  latestAttempt: WorkItemAttempt | null = null;

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

  resetForFreshAttempt(): void {
    this.status = 'pending';
    this.attempts = 0;
    this.duration = 0;
    this.artifacts = [];
    this.output = null;
    this.reason = null;
    this.causedBy = null;
    this.latestAttempt = null;
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
      latestAttempt: this.latestAttempt,
    };
  }

  startWorkAttempt(now: string, evidence: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> = {}): WorkItemAttempt {
    this.status = 'running';
    const attemptNumber = this.latestAttempt ? this.latestAttempt.attemptNumber + 1 : 1;
    this.latestAttempt = {
      state: 'running',
      attemptNumber,
      startedAt: now,
      completedAt: null,
      output: null,
      error: null,
      diagnostic: null,
      queueTaskId: evidence.queueTaskId ?? null,
      acpSessionId: evidence.acpSessionId ?? null,
      coderSessionId: evidence.coderSessionId ?? null,
      executionId: evidence.executionId ?? null,
      processPid: evidence.processPid ?? null,
    };
    return this.latestAttempt;
  }

  completeWorkAttempt(result: { output?: unknown; artifacts?: string[]; duration?: number; reason?: string }, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'completed';
    this.attempts = this.latestAttempt.attemptNumber;
    this.output = result.output ?? this.output;
    this.artifacts = result.artifacts ?? this.artifacts;
    this.duration = result.duration ?? this.duration;
    this.reason = result.reason ?? this.reason;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'completed',
      completedAt: now,
      output: result.output ?? null,
    };
    return this.latestAttempt;
  }

  failWorkAttempt(error: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'failed';
    this.attempts = this.latestAttempt.attemptNumber;
    this.reason = error;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'failed',
      completedAt: now,
      error,
      diagnostic,
    };
    return this.latestAttempt;
  }

  interruptWorkAttempt(reason: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'pending';
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'interrupted',
      completedAt: now,
      error: reason,
      diagnostic,
    };
    return this.latestAttempt;
  }

  synthesizeLatestAttempt(now: string): void {
    if (this.latestAttempt) return;
    if (this.status === 'completed') {
      this.latestAttempt = {
        state: 'completed',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'failed' || this.status === 'skipped') {
      this.latestAttempt = {
        state: 'failed',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: this.reason,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'running') {
      this.latestAttempt = {
        state: 'running',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: null,
        output: null,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    }
  }
}

export class CheckState {
  status: CheckRunStatus = 'pending';
  message: string | null = null;
  output: unknown | null = null;
  runCount = 0;
  latestAttempt: WorkItemAttempt | null = null;

  constructor(
    readonly name: string,
    readonly title: string,
  ) {}

  resetForFreshAttempt(): void {
    this.status = 'pending';
    this.message = null;
    this.output = null;
    this.runCount = 0;
    this.latestAttempt = null;
  }

  snapshot(): CheckStateSnapshot {
    return {
      name: this.name,
      title: this.title,
      status: this.status,
      message: this.message,
      output: this.output,
      runCount: this.runCount,
      latestAttempt: this.latestAttempt,
    };
  }

  startWorkAttempt(now: string, evidence: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> = {}): WorkItemAttempt {
    this.status = 'running';
    const attemptNumber = this.latestAttempt ? this.latestAttempt.attemptNumber + 1 : 1;
    this.latestAttempt = {
      state: 'running',
      attemptNumber,
      startedAt: now,
      completedAt: null,
      output: null,
      error: null,
      diagnostic: null,
      queueTaskId: evidence.queueTaskId ?? null,
      acpSessionId: evidence.acpSessionId ?? null,
      coderSessionId: evidence.coderSessionId ?? null,
      executionId: evidence.executionId ?? null,
      processPid: evidence.processPid ?? null,
    };
    return this.latestAttempt;
  }

  completeWorkAttempt(now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'passed';
    this.runCount = this.latestAttempt.attemptNumber;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'completed',
      completedAt: now,
    };
    return this.latestAttempt;
  }

  failWorkAttempt(error: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'failed';
    this.runCount = this.latestAttempt.attemptNumber;
    this.message = error;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'failed',
      completedAt: now,
      error,
      diagnostic,
    };
    return this.latestAttempt;
  }

  interruptWorkAttempt(reason: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'pending';
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'interrupted',
      completedAt: now,
      error: reason,
      diagnostic,
    };
    return this.latestAttempt;
  }

  synthesizeLatestAttempt(now: string): void {
    if (this.latestAttempt) return;
    if (this.status === 'passed') {
      this.latestAttempt = {
        state: 'completed',
        attemptNumber: Math.max(1, this.runCount),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'failed' || this.status === 'error') {
      this.latestAttempt = {
        state: 'failed',
        attemptNumber: Math.max(1, this.runCount),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: this.message,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'running') {
      this.latestAttempt = {
        state: 'running',
        attemptNumber: Math.max(1, this.runCount),
        startedAt: now,
        completedAt: null,
        output: null,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    }
  }
}

export class StageRun {
  readonly tasks: TaskRun[];
  readonly checks: CheckState[];
  status: StageRunStatus = 'pending';
  attemptSequence = 1;
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
    task.resetForFreshAttempt();
  }

  resetCheck(checkName: string): void {
    const check = this.findCheck(checkName);
    check.resetForFreshAttempt();
  }

  resetTaskAndDownstream(taskId: string): void {
    const task = this.findTask(taskId);
    const boundaryOrder = task.order;

    for (const t of this.tasks) {
      if (t.order >= boundaryOrder) {
        t.resetForFreshAttempt();
      }
    }
  }

  resetCheckAndDownstream(checkName: string): void {
    const boundaryIndex = this.checks.findIndex(c => c.name === checkName);

    for (const [index, c] of this.checks.entries()) {
      if (index >= boundaryIndex) {
        c.resetForFreshAttempt();
      }
    }

    for (const t of this.tasks) {
      if (t.causedBy?.type === 'check-failure' && t.causedBy.checkName === checkName) {
        t.resetForFreshAttempt();
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

  removeNonStaticTasks(): void {
    const staticTaskIds = new Set(this.definition.tasks.map(task => task.id));
    for (let index = this.tasks.length - 1; index >= 0; index--) {
      if (!staticTaskIds.has(this.tasks[index].id)) {
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
    this.buildWorkSourceState = { evaluated: true, missing: true };
  }

  recordBuildWorkSourceInvalid(): void {
    if (this.stage !== Stage.Build) return;
    this.buildWorkSourceState = { evaluated: true, invalid: true };
  }

  recordBuildWorkSourceEmpty(): void {
    if (this.stage !== Stage.Build) return;
    this.buildWorkSourceState = { evaluated: true, empty: true };
  }

  resetBuildWorkSourceState(): void {
    if (this.stage !== Stage.Build) return;
    this.buildWorkSourceState = { evaluated: false };
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
      attemptSequence: this.attemptSequence,
      tasks: this.tasks.map(task => task.snapshot()),
      checks: this.checks.map(check => check.snapshot()),
      approval: this.approval ? { ...this.approval } : null,
      failure: this.failure,
      freezePoint: this.freezePoint,
      buildWorkSourceState: this.stage === Stage.Build ? this.buildWorkSourceState : undefined,
    };
  }
}



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
    readonly workflowDefinitionSnapshot: WorkflowDefinitionSnapshot,
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
    workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot;
    now?: string;
  }): { run: WorkflowRun; decision: WorkflowDecision } {
    const workflowDefinitionSnapshot = input.workflowDefinitionSnapshot
      ? cloneWorkflowDefinitionSnapshot(input.workflowDefinitionSnapshot)
      : input.definitions
        ? createWorkflowDefinitionSnapshot({
          definition: {
            id: 'runtime/custom',
            name: 'Runtime custom workflow',
            stages: input.definitions,
          },
          source: { type: 'runtime', id: 'runtime/custom' },
          capturedAt: input.now,
        })
        : createDefaultWorkflowDefinitionSnapshot(input.now);
    const run = new WorkflowRun(
      input.id,
      input.issueId,
      input.issueNumber,
      input.definitions ?? workflowDefinitionSnapshot.compiledStageDefinitions ?? DEFAULT_STAGE_DEFINITIONS,
      workflowDefinitionSnapshot,
    );
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

    if (task.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (result.status === 'completed') {
        task.completeWorkAttempt({ output: result.output, artifacts: result.artifacts, duration: result.duration }, attemptNow);
      } else if (result.status === 'failed' || result.status === 'skipped') {
        task.failWorkAttempt(result.reason ?? 'Task failed', null, attemptNow);
      }
    }

    if (this.taskLocksCode(stageRun, taskId) && result.status === 'completed') {
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

    if (check.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (result.status === 'pass') {
        check.completeWorkAttempt(attemptNow);
      } else if (result.status === 'fail' || result.status === 'error') {
        check.failWorkAttempt(result.message ?? `Check ${result.name} failed`, null, attemptNow);
      }
    }

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

    if (stageRun.freezePoint) {
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
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApprovalEvidence: false });
    if (!guard.complete) {
      return { events: [], nextWork: { kind: 'blocked', stage, reason: guard } };
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

  startTaskAttempt(stage: Stage, taskId: string, now: string, evidence?: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>>): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRun(stage);
    const task = stageRun.findTask(taskId);
    task.startWorkAttempt(now, evidence);
  }

  startCheckAttempt(stage: Stage, checkName: string, now: string, evidence?: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>>): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRun(stage);
    const check = stageRun.findCheck(checkName);
    check.startWorkAttempt(now, evidence);
  }

  interruptSpecificWorkAttempts(attempts: WorkItemAttempt[], reason: string, diagnostic: string | null = null): void {
    if (attempts.length === 0) return;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return;
    const now = new Date().toISOString();
    const pending = new Set(attempts);
    let interrupted = 0;

    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running' && pending.has(task.latestAttempt)) {
        task.interruptWorkAttempt(reason, diagnostic, now);
        pending.delete(task.latestAttempt);
        interrupted++;
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running' && pending.has(check.latestAttempt)) {
        check.interruptWorkAttempt(reason, diagnostic, now);
        pending.delete(check.latestAttempt);
        interrupted++;
      }
    }
    if (interrupted > 0) this.markWaitingForRecovery(stageRun, reason, diagnostic);
  }

  interruptRunningWorkAttempts(reason: string, diagnostic: string | null = null): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return;
    const now = new Date().toISOString();
    let interrupted = 0;
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running') {
        task.interruptWorkAttempt(reason, diagnostic, now);
        interrupted++;
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') {
        check.interruptWorkAttempt(reason, diagnostic, now);
        interrupted++;
      }
    }
    if (interrupted > 0) this.markWaitingForRecovery(stageRun, reason, diagnostic);
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
    stageRun.attemptSequence += 1;
    const wasApprovalRejected = (stageFailureReason ?? runFailureReason) === 'approval-rejected';
    const failedTask = stageRun.tasks.find(t => t.status === 'failed' || t.status === 'skipped');
    const failedCheck = stageRun.checks.find(c => c.status === 'failed' || c.status === 'error');
    stageRun.failure = null;
    stageRun.approval = null;

    if (wasApprovalRejected) {
      for (const task of stageRun.tasks) {
        task.resetForFreshAttempt();
      }
      for (const check of stageRun.checks) {
        check.resetForFreshAttempt();
      }
    } else {
      if (failedTask) {
        stageRun.resetTaskAndDownstream(failedTask.id);
        for (const check of stageRun.checks) {
          check.resetForFreshAttempt();
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
              task.resetForFreshAttempt();
            }
          }
        }
        stageRun.resetCheckAndDownstream(failedCheck.name);
      } else {
        for (const task of stageRun.tasks) {
          if (!task.terminal) {
            task.resetForFreshAttempt();
          }
        }
        for (const check of stageRun.checks) {
          check.resetForFreshAttempt();
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
    if (this.findCurrentStageInterruptedAttempt(stageRun)) return false;
    if ((stageRun.failure?.reason ?? this.failure?.reason) === 'approval-rejected') return true;
    return this.findCurrentStageFailedAttempt(stageRun) !== null;
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
    stageRun.attemptSequence += 1;
    stageRun.failure = null;
    stageRun.approval = null;

    stageRun.removeGeneratedTasks();
    if (stage === Stage.Build) {
      stageRun.removeNonStaticTasks();
      stageRun.resetBuildWorkSourceState();
    }

    for (const task of stageRun.tasks) {
      task.resetForFreshAttempt();
    }
    for (const check of stageRun.checks) {
      check.resetForFreshAttempt();
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
    const buildWorkSourceFailure = this.evaluateBuildWorkSourceFailureGuard(stageRun);
    if (buildWorkSourceFailure) return { kind: 'blocked', stage: stageRun.stage, reason: buildWorkSourceFailure };
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
      workflowDefinitionSnapshot: cloneWorkflowDefinitionSnapshot(this.workflowDefinitionSnapshot),
      stageRuns: this.stageRuns.map(stageRun => stageRun.snapshot()),
      failure: this.failure,
    };
  }

  workflowRecoverySummary(): WorkflowRecoverySummary {
    if (this.status === 'passed') return 'completed';

    const stageRun = this.currentStageRun();
    if (!stageRun) {
      return this.status === 'failed' ? 'waiting-for-recovery' : 'running';
    }

    if (stageRun.status === 'awaiting-approval') return 'awaiting-approval';

    const latestRunningAttempt = this.findCurrentStageRunningAttempt(stageRun);
    if (latestRunningAttempt) return 'running';

    const failedTask = stageRun.tasks.find(t => t.status === 'failed' || t.status === 'skipped');
    const failedCheck = stageRun.checks.find(c => c.status === 'failed' || c.status === 'error');
    if (failedTask || failedCheck) return 'waiting-for-recovery';

    const interruptedAttempt = this.findCurrentStageInterruptedAttempt(stageRun);
    if (interruptedAttempt) return 'waiting-for-recovery';

    const currentWorkItem = this.findCurrentStagePendingWorkItem(stageRun);
    if (currentWorkItem?.latestAttempt?.state === 'failed') return 'waiting-for-recovery';
    if (currentWorkItem?.latestAttempt === null && currentWorkItem.causedBy) return 'waiting-for-recovery';

    if (this.status === 'failed') return 'waiting-for-recovery';

    return 'running';
  }

  private findCurrentStagePendingWorkItem(stageRun: StageRun): {
    latestAttempt: WorkItemAttempt | null;
    causedBy?: CausedByMetadata | null;
  } | null {
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) return { latestAttempt: preTaskCheck.latestAttempt };
    const task = stageRun.nextTask();
    if (task) return { latestAttempt: task.latestAttempt, causedBy: task.causedBy };
    const postTaskCheck = stageRun.nextCheck('post-task');
    if (postTaskCheck) return { latestAttempt: postTaskCheck.latestAttempt };
    return null;
  }

  private findCurrentStageRunningAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') return check.latestAttempt;
    }
    return null;
  }

  private findCurrentStageInterruptedAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'interrupted') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'interrupted') return check.latestAttempt;
    }
    return null;
  }

  private findCurrentStageFailedAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'failed') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'failed') return check.latestAttempt;
    }
    return null;
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
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApprovalEvidence: false });
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

  private evaluateBuildWorkSourceFailureGuard(stageRun: StageRun): StageCompletionGuard | null {
    if (stageRun.stage !== Stage.Build) return null;
    if (!(stageRun.definition.workSources ?? []).some(source => source.kind !== 'static' && source.kind !== 'runtime')) return null;
    const state: BuildWorkSourceState = stageRun.buildWorkSourceState;
    if (!state.evaluated) return { complete: false, reason: 'dynamic-source-not-evaluated', stage: Stage.Build };
    if ('missing' in state && state.missing) return { complete: false, reason: 'dynamic-source-missing', stage: Stage.Build };
    if ('invalid' in state && state.invalid) return { complete: false, reason: 'dynamic-source-invalid', stage: Stage.Build };
    if ('empty' in state && state.empty) return { complete: false, reason: 'dynamic-source-empty', stage: Stage.Build };
    return null;
  }

  private evaluateStageCompletionGuard(
    stageRun: StageRun,
    options: { includeApprovalEvidence?: boolean } = {},
  ): StageCompletionGuard {
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

    if (stageRun.stage === Stage.Build && (stageRun.definition.workSources ?? []).some(source => source.kind !== 'static' && source.kind !== 'runtime')) {
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

    const deliveryEvidenceGuard = this.evaluateDeliveryEvidenceGuard(stageRun);
    if (!deliveryEvidenceGuard.complete) return deliveryEvidenceGuard;

    for (const taskRun of stageRun.tasks) {
      if (!taskRun.terminal) return { complete: false, reason: 'run-task-pending', taskId: taskRun.id };
    }

    if ((options.includeApprovalEvidence ?? true) && stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      return { complete: false, reason: 'approval-required', stage: stageRun.stage };
    }

    return { complete: true };
  }

  private evaluateDeliveryEvidenceGuard(stageRun: StageRun): StageCompletionGuard {
    for (const requirement of stageRun.definition.evidenceRequirements ?? []) {
      const taskRun = requirement.taskId ? stageRun.tasks.find(task => task.id === requirement.taskId) : null;
      if (requirement.taskId && taskRun?.status !== 'completed') {
        return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, taskId: requirement.taskId, uses: requirement.uses };
      }
      if (taskRun) {
        const evidence = validateWorkflowUseEvidence(requirement.uses ?? this.taskUse(stageRun, taskRun.id), taskRun.output);
        if (!evidence.ok) {
          return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, taskId: taskRun.id, uses: requirement.uses };
        }
      }

      const checkRun = requirement.checkName ? stageRun.checks.find(check => check.name === requirement.checkName) : null;
      if (requirement.checkName && checkRun?.status !== 'passed') {
        return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, checkName: requirement.checkName, uses: requirement.uses };
      }
      if (!requirement.taskId && !requirement.checkName && requirement.uses) {
        const matchingTask = stageRun.tasks.find(task => this.taskUse(stageRun, task.id) === requirement.uses);
        if (!matchingTask || matchingTask.status !== 'completed') {
          return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, uses: requirement.uses };
        }
        const evidence = validateWorkflowUseEvidence(requirement.uses, matchingTask.output);
        if (!evidence.ok) {
          return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, taskId: matchingTask.id, uses: requirement.uses };
        }
      }
    }
    return { complete: true };
  }

  private taskUse(stageRun: StageRun, taskId: string): string {
    const taskDefinition = stageRun.definition.tasks.find(task => task.id === taskId);
    const policy = stageRun.definition.taskExecutionPolicies?.find(candidate => candidate.taskId === taskId)
      ?? stageRun.definition.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
    return taskDefinition?.uses ?? inferWorkflowTaskUse(taskId, policy?.kind);
  }

  private taskLocksCode(stageRun: StageRun, taskId: string): boolean {
    const use = getWorkflowUseDefinition(this.taskUse(stageRun, taskId));
    return use?.locksCode === true;
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
    const guard = this.evaluateStageCompletionGuard(stageRun);
    if (!guard.complete) return this.decision(events);

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

  private markWaitingForRecovery(stageRun: StageRun, reason: string, diagnostic: string | null): void {
    const failure: FailureDetails = {
      reason: 'work-interrupted',
      stage: stageRun.stage,
      message: diagnostic ?? reason,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
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
    const baseTaskId = this.baseRuntimeTaskId(taskId);

    for (const entry of policy.entries) {
      if (entry.trigger !== 'task-completion') continue;
      if (entry.triggerTaskId && entry.triggerTaskId !== taskId && entry.triggerTaskId !== baseTaskId) continue;
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

  private baseRuntimeTaskId(taskId: string): string {
    return taskId.replace(/:\d+$/, '');
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
