import { Stage } from '../../types';
import { DEFAULT_STAGE_DEFINITIONS, createDefaultWorkflowDefinitionSnapshot } from './default-workflow';
import { WorkflowDomainError } from './errors';
import { cloneWorkflowDefinitionSnapshot, createWorkflowDefinitionSnapshot } from './workflow-definition';
import { getWorkflowUseDefinition, inferWorkflowCheckUse, inferWorkflowTaskUse, validateWorkflowUseEvidence } from '../uses-catalog';
import type {
  ApprovalInput,
  ApprovalSnapshot,
  CausedByMetadata,
  CheckFailurePolicy,
  CheckPhase,
  CheckPolicy,
  CheckResultInput,
  CheckRunStatus,
  CheckStateSnapshot,
  CompiledStageDefinition,
  DeliveryMetadata,
  FailureDetails,
  FreezePoint,
  MaterializedTaskInput,
  StageCompletionGuard,
  StageRunSnapshot,
  StageRunStatus,
  TaskResetMetadata,
  TaskResultInput,
  TaskRunSnapshot,
  TaskRunStatus,
  WorkItemAttempt,
  WorkSourceState,
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
  definitions: CompiledStageDefinition[] = DEFAULT_STAGE_DEFINITIONS,
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
  events: string[] = [];
  output: unknown | null = null;
  reason: string | null = null;
  causedBy: CausedByMetadata | null = null;
  resetBy: TaskResetMetadata | null = null;
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

  resetForFreshAttempt(resetBy: TaskResetMetadata | null = null): void {
    this.status = 'pending';
    this.attempts = 0;
    this.duration = 0;
    this.artifacts = [];
    this.events = [];
    this.output = null;
    this.reason = null;
    this.causedBy = null;
    this.resetBy = resetBy;
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
      events: [...this.events],
      output: this.output,
      reason: this.reason,
      causedBy: this.causedBy,
      resetBy: this.resetBy,
      latestAttempt: this.latestAttempt,
    };
  }

  startWorkAttempt(now: string, evidence: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> = {}): WorkItemAttempt {
    this.status = 'running';
    this.resetBy = null;
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

  completeWorkAttempt(result: { output?: unknown; artifacts?: string[]; events?: string[]; duration?: number; reason?: string }, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'completed';
    this.attempts = this.latestAttempt.attemptNumber;
    this.output = result.output ?? this.output;
    this.artifacts = result.artifacts ?? this.artifacts;
    this.events = result.events ?? this.events;
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
  workSourceState: WorkSourceState = { evaluated: false };

  constructor(
    readonly definition: CompiledStageDefinition,
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

  get buildWorkSourceState(): WorkSourceState {
    return this.workSourceState;
  }

  set buildWorkSourceState(state: WorkSourceState) {
    this.workSourceState = state;
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

  resetTask(taskId: string, resetBy: TaskResetMetadata | null = null): void {
    const task = this.findTask(taskId);
    task.resetForFreshAttempt(resetBy);
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
    const suffix = this.scheduledFixCount(policy.checkName);
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
    const staticTaskIds = new Set(this.definition.tasks.map(task => task.id));
    for (let index = this.tasks.length - 1; index >= 0; index--) {
      const task = this.tasks[index];
      const isRepairTask = [...repairTaskIds].some(fixTaskId => task.id === fixTaskId || task.id.startsWith(`${fixTaskId}:`));
      const isRuntimeTask = (!staticTaskIds.has(task.id) && task.causedBy !== null) || task.id === 'rebase-branch';
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

  recordWorkSourceEvaluated(tasks: MaterializedTaskInput[]): void {
    this.workSourceState = { evaluated: true, tasks };
  }

  recordWorkSourceMissing(): void {
    this.workSourceState = { evaluated: true, missing: true };
  }

  recordWorkSourceInvalid(): void {
    this.workSourceState = { evaluated: true, invalid: true };
  }

  recordWorkSourceEmpty(): void {
    this.workSourceState = { evaluated: true, empty: true };
  }

  resetWorkSourceState(): void {
    this.workSourceState = { evaluated: false };
  }

  recordBuildWorkSourceEvaluated(tasks: MaterializedTaskInput[]): void {
    this.recordWorkSourceEvaluated(tasks);
  }

  recordBuildWorkSourceMissing(): void {
    this.recordWorkSourceMissing();
  }

  recordBuildWorkSourceInvalid(): void {
    this.recordWorkSourceInvalid();
  }

  recordBuildWorkSourceEmpty(): void {
    this.recordWorkSourceEmpty();
  }

  resetBuildWorkSourceState(): void {
    this.resetWorkSourceState();
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
      attemptSequence: this.attemptSequence,
      tasks: this.tasks.map(task => task.snapshot()),
      checks: this.checks.map(check => check.snapshot()),
      approval: this.approval ? { ...this.approval } : null,
      failure: this.failure,
      freezePoint: this.freezePoint,
      workSourceState: this.hasDynamicWorkSource() ? this.workSourceState : undefined,
      buildWorkSourceState: this.stage === Stage.Build ? this.workSourceState : undefined,
    };
  }

  hasDynamicWorkSource(): boolean {
    return (this.definition.workSources ?? []).some(source => source.kind !== 'static' && source.kind !== 'runtime');
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
    readonly definitions: CompiledStageDefinition[],
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
    definitions?: CompiledStageDefinition[];
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

  materializeTasks(stage: Stage, tasks: MaterializedTaskInput[], workSourceState?: 'missing' | 'invalid' | 'empty'): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    stageRun.materializeTasks(tasks);
    if (stageRun.hasDynamicWorkSource()) {
      if (workSourceState === 'missing') {
        stageRun.recordWorkSourceMissing();
      } else if (workSourceState === 'invalid') {
        stageRun.recordWorkSourceInvalid();
      } else if (workSourceState === 'empty') {
        stageRun.recordWorkSourceEmpty();
      } else if (tasks.length === 0) {
        stageRun.recordWorkSourceEmpty();
      } else {
        stageRun.recordWorkSourceEvaluated(tasks);
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

    const evidenceFailure = result.status === 'completed'
      ? this.workflowUseEvidenceFailure(this.taskUse(stageRun, taskId), result.output)
      : null;
    const effectiveResult: TaskResultInput = evidenceFailure
      ? {
        ...result,
        status: 'failed',
        reason: evidenceFailure,
        causedBy: result.causedBy ?? { type: 'system-policy', taskId, message: evidenceFailure },
      }
      : result;
    if (effectiveResult.status === 'completed') {
      effectiveResult.events = this.mergeTaskEvents(effectiveResult.events, this.taskSuccessEvents(stageRun, taskId));
    }

    task.status = effectiveResult.status;
    task.attempts = effectiveResult.attempts ?? task.attempts + 1;
    task.duration = effectiveResult.duration ?? task.duration;
    task.artifacts = effectiveResult.artifacts ?? task.artifacts;
    task.events = effectiveResult.events ?? task.events;
    task.output = effectiveResult.output ?? task.output;
    task.reason = effectiveResult.reason ?? task.reason;
    task.causedBy = effectiveResult.causedBy ?? task.causedBy;
    task.resetBy = null;

    if (task.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (effectiveResult.status === 'completed') {
        task.completeWorkAttempt({ output: effectiveResult.output, artifacts: effectiveResult.artifacts, events: effectiveResult.events, duration: effectiveResult.duration }, attemptNow);
      } else if (effectiveResult.status === 'failed' || effectiveResult.status === 'skipped') {
        task.failWorkAttempt(effectiveResult.reason ?? 'Task failed', null, attemptNow);
      }
    }

    if (this.taskLocksCode(stageRun, taskId) && effectiveResult.status === 'completed') {
      stageRun.freezePoint = {
        taskId,
        delivery: this.extractDeliveryMetadata(effectiveResult.output),
        frozenAt: new Date().toISOString(),
      };
    }

    if (effectiveResult.status === 'failed' || effectiveResult.status === 'skipped') {
      const failure: FailureDetails = {
        reason: 'task-failed',
        stage,
        taskId,
        message: effectiveResult.reason,
        causedBy: effectiveResult.causedBy,
      };
      return this.fail(stageRun, failure, [
        { type: 'task-failed', stage, taskId, reason: failure },
      ]);
    }

    const events: WorkflowEvent[] = [{ type: 'task-completed', stage, taskId }];
    const invalidationEvents = this.applyTaskCompletionInvalidation(stageRun, taskId, effectiveResult);
    events.push(...invalidationEvents);
    if (stageRun.freezePoint) events.push({ type: 'delivery-frozen', stage, freezePoint: stageRun.freezePoint });
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

    const normalizedOutput = result.output ?? null;
    const evidenceFailure = result.status === 'pass'
      ? this.workflowUseEvidenceFailure(this.checkUse(stageRun, result.name), normalizedOutput)
      : null;
    const effectiveStatus: CheckResultInput['status'] = evidenceFailure ? 'fail' : result.status;
    const effectiveMessage = evidenceFailure ?? result.message;

    check.status = this.toCheckStatus(effectiveStatus);
    check.message = effectiveMessage ?? null;
    check.output = normalizedOutput;
    if (effectiveStatus !== 'pending') check.runCount += 1;

    if (check.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (effectiveStatus === 'pass') {
        check.completeWorkAttempt(attemptNow);
      } else if (effectiveStatus === 'fail' || effectiveStatus === 'error') {
        check.failWorkAttempt(effectiveMessage ?? `Check ${result.name} failed`, null, attemptNow);
      }
    }

    if (this.checkLocksCode(stageRun, check.name) && effectiveStatus === 'pass') {
      stageRun.freezePoint = {
        checkName: check.name,
        delivery: this.extractDeliveryMetadata(normalizedOutput),
        frozenAt: new Date().toISOString(),
      };
    }

    const events: WorkflowEvent[] = [{ type: 'check-recorded', stage, checkName: check.name, status: check.status }];
    if (effectiveStatus === 'pending' || effectiveStatus === 'pass') {
      if (stageRun.freezePoint) events.push({ type: 'delivery-frozen', stage, freezePoint: stageRun.freezePoint });
      return this.maybeCompleteStage(stageRun, events);
    }

    if (stageRun.freezePoint) {
      return this.fail(stageRun, {
        reason: 'post-delivery-check-failed',
        stage,
        checkName: result.name,
        message: effectiveMessage,
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
        message: effectiveMessage,
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
      message: effectiveMessage,
      causedBy: { type: 'check-failure', checkName: result.name, message: effectiveMessage },
    }, events);
  }

  approveStage(stage: Stage, input: ApprovalInput = {}): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'awaiting-approval' || !stageRun.approval) {
      throw new WorkflowDomainError(`Stage ${stage} is not awaiting approval`);
    }
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApproval: false });
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
        const retryInvalidationEvents = this.applyRetryInvalidationForCompletedTasks(stageRun);
        if (retryInvalidationEvents.length > 0) {
          return this.decision([
            { type: 'stage-retried', stage },
            ...retryInvalidationEvents,
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

  private applyRetryInvalidationForCompletedTasks(stageRun: StageRun): WorkflowEvent[] {
    const policy = stageRun.definition.invalidationPolicy;
    if (!policy) return [];

    const events: WorkflowEvent[] = [];
    for (const task of stageRun.tasks) {
      if (task.status !== 'completed') continue;
      const baseTaskId = this.baseRuntimeTaskId(task.id);
      const raisedEvents = new Set(task.events);
      for (const entry of policy.entries) {
        if (entry.trigger !== 'task-completion') continue;
        if (entry.triggerTaskId && entry.triggerTaskId !== task.id && entry.triggerTaskId !== baseTaskId) continue;
        if (entry.eventName && !raisedEvents.has(entry.eventName)) continue;
        const reason = entry.reason ?? `Policy invalidation while retrying after ${task.id}`;
        for (const taskId of entry.invalidates.tasks ?? []) {
          try {
            stageRun.resetTask(taskId, {
              type: 'workflow-policy',
              taskId: task.id,
              eventName: entry.eventName,
              message: reason,
            });
            events.push({ type: 'task-invalidated', stage: stageRun.stage, taskId, reason });
          } catch {
            // Task may not belong to this stage definition.
          }
        }
        for (const checkName of entry.invalidates.checks ?? []) {
          try {
            stageRun.resetCheck(checkName);
            events.push({ type: 'check-invalidated', stage: stageRun.stage, checkName, reason });
          } catch {
            // Check may not belong to this stage definition.
          }
        }
        if (entry.invalidates.approval && stageRun.approval) {
          stageRun.approval = null;
          if (stageRun.status === 'awaiting-approval') {
            stageRun.status = 'running';
          }
        }
      }
    }
    return events;
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
    if (stageRun.hasDynamicWorkSource()) {
      stageRun.removeNonStaticTasks();
      stageRun.resetWorkSourceState();
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
    const workSourceFailure = this.evaluateWorkSourceFailureGuard(stageRun);
    if (workSourceFailure) return { kind: 'blocked', stage: stageRun.stage, reason: workSourceFailure };
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
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApproval: false });
    if (!guard.complete) return this.decision(events);
    if (stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      if (!stageRun.approval) {
        stageRun.requestApproval(new Date().toISOString(), this.buildApprovalOutput(stageRun));
        events.push({ type: 'approval-requested', stage: stageRun.stage });
      } else if (stageRun.approval.status === 'awaiting') {
        stageRun.status = 'awaiting-approval';
      }
      return this.decision(events);
    }
    return this.completeStage(stageRun, events);
  }

  private evaluateWorkSourceFailureGuard(stageRun: StageRun): StageCompletionGuard | null {
    if (!stageRun.hasDynamicWorkSource()) return null;
    const state: WorkSourceState = stageRun.workSourceState;
    if (!state.evaluated) return { complete: false, reason: 'dynamic-source-not-evaluated', stage: stageRun.stage };
    if ('missing' in state && state.missing) return { complete: false, reason: 'dynamic-source-missing', stage: stageRun.stage };
    if ('invalid' in state && state.invalid) return { complete: false, reason: 'dynamic-source-invalid', stage: stageRun.stage };
    if ('empty' in state && state.empty) return { complete: false, reason: 'dynamic-source-empty', stage: stageRun.stage };
    return null;
  }

  private evaluateStageCompletionGuard(
    stageRun: StageRun,
    options: { includeApproval?: boolean } = {},
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

    const workSourceGuard = this.evaluateWorkSourceFailureGuard(stageRun);
    if (workSourceGuard) return workSourceGuard;

    const deliveryEvidenceGuard = this.evaluateDeliveryEvidenceGuard(stageRun);
    if (!deliveryEvidenceGuard.complete) return deliveryEvidenceGuard;

    for (const taskRun of stageRun.tasks) {
      if (!taskRun.terminal) return { complete: false, reason: 'run-task-pending', taskId: taskRun.id };
    }

    if ((options.includeApproval ?? true) && stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      return { complete: false, reason: 'approval-required', stage: stageRun.stage };
    }

    return { complete: true };
  }

  private evaluateDeliveryEvidenceGuard(stageRun: StageRun): StageCompletionGuard {
    for (const taskRun of stageRun.tasks) {
      if (taskRun.status !== 'completed') continue;
      const uses = this.taskUse(stageRun, taskRun.id);
      const evidence = validateWorkflowUseEvidence(uses, taskRun.output);
      if (!evidence.ok) {
        return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, taskId: taskRun.id, uses };
      }
    }
    for (const checkRun of stageRun.checks) {
      if (checkRun.status !== 'passed') continue;
      const uses = this.checkUse(stageRun, checkRun.name);
      const evidence = validateWorkflowUseEvidence(uses, checkRun.output);
      if (!evidence.ok) {
        return { complete: false, reason: 'delivery-evidence-missing', stage: stageRun.stage, checkName: checkRun.name, uses };
      }
    }
    return { complete: true };
  }

  private workflowUseEvidenceFailure(uses: string, output: unknown): string | null {
    const evidence = validateWorkflowUseEvidence(uses, output);
    if (evidence.ok) return null;
    if (evidence.reason === 'unknown-use') return `Unknown workflow use ${uses}`;
    return `Missing required evidence for ${uses}: ${evidence.field ?? 'output'}`;
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

  private checkUse(stageRun: StageRun, checkName: string): string {
    const checkDefinition = stageRun.definition.checks.find(check => check.name === checkName);
    return checkDefinition?.uses ?? inferWorkflowCheckUse(checkName);
  }

  private checkLocksCode(stageRun: StageRun, checkName: string): boolean {
    const use = getWorkflowUseDefinition(this.checkUse(stageRun, checkName));
    return use?.locksCode === true;
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

  private buildApprovalOutput(stageRun: StageRun): unknown {
    const approvalCheckName = stageRun.definition.approvalPolicy?.checkName ?? stageRun.definition.approvalCheckName;
    if (!approvalCheckName) return null;

    const passedChecks = stageRun.checks
      .filter(check => check.status === 'passed')
      .map(check => ({
        name: check.name,
        output: check.output,
      }));
    return {
      result: 'PASS',
      checks: passedChecks,
    };
  }

  private toCheckStatus(status: CheckResultInput['status']): CheckRunStatus {
    if (status === 'pass') return 'passed';
    if (status === 'fail') return 'failed';
    return status;
  }

  private applyTaskCompletionInvalidation(stageRun: StageRun, taskId: string, result: TaskResultInput): WorkflowEvent[] {
    const events: WorkflowEvent[] = [];
    const policy = stageRun.definition.invalidationPolicy;
    if (!policy) return events;
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    const raisedEvents = new Set(result.events ?? []);

    for (const entry of policy.entries) {
      if (entry.trigger !== 'task-completion') continue;
      if (entry.triggerTaskId && entry.triggerTaskId !== taskId && entry.triggerTaskId !== baseTaskId) continue;
      if (entry.eventName && !raisedEvents.has(entry.eventName)) continue;

      if (entry.invalidates.tasks) {
        for (const t of entry.invalidates.tasks) {
          try {
            const reason = entry.reason ?? `Policy invalidation after ${taskId}`;
            stageRun.resetTask(t, {
              type: 'workflow-policy',
              taskId,
              eventName: entry.eventName,
              message: reason,
            });
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

  private taskSuccessEvents(stageRun: StageRun, taskId: string): string[] {
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    const taskDefinitions = [
      ...stageRun.definition.tasks,
      ...stageRun.definition.checks.flatMap(check => check.onFailure?.retry?.task ? [check.onFailure.retry.task] : []),
    ];
    for (const task of taskDefinitions) {
      if (task.id === taskId || task.id === baseTaskId) {
        return task.onSuccess?.emit ?? [];
      }
    }
    return [];
  }

  private mergeTaskEvents(resultEvents: string[] | undefined, configuredEvents: string[]): string[] | undefined {
    const merged = new Set<string>(resultEvents ?? []);
    for (const eventName of configuredEvents) {
      merged.add(eventName);
    }
    return merged.size > 0 ? [...merged] : undefined;
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
      landedSha: typeof data.landedSha === 'string' ? data.landedSha : typeof data.mergedSha === 'string' ? data.mergedSha : undefined,
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
