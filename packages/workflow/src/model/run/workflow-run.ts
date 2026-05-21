import type { StageDefinition, WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { ApprovalInput, CheckResultInput, FailureDetails, MaterializedTaskInput, StageRunState, TaskResultInput, WorkflowRunStatus, WorkflowWork } from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'pending';
  currentStage: StageRun;
  failure: FailureDetails | null = null;
  pauseRequested = false;

  constructor(
    readonly id: string,
    readonly definitionStages: StageDefinition[],
  ) {
    if (definitionStages.length === 0) {
      throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    }
    this.stageRuns = definitionStages.map((definition, index) => new StageRun(definition, index));
    this.currentStage = this.stageRuns[0];
  }

  get stageOrder(): WorkflowStageId[] {
    return this.definitionStages.map(definition => definition.stage);
  }

  get stages(): StageRunState[] {
    return this.stageRuns.map(stageRun => ({
      stage: stageRun.stage,
      status: stageRun.status,
      order: stageRun.order,
      tasks: stageRun.tasks.map(taskRun => ({
        id: taskRun.id,
        title: taskRun.title,
        uses: taskRun.uses,
        with: taskRun.withInput,
        status: taskRun.status,
      })),
      checks: stageRun.checks.map(check => ({
        name: check.name,
        title: check.title,
        uses: check.uses,
        with: check.withInput,
        status: check.status,
        message: check.message,
        output: check.output,
      })),
      approval: stageRun.approval ? { ...stageRun.approval } : null,
      failure: stageRun.failure,
    }));
  }

  start(): void {
    if (this.status !== 'pending' && this.status !== 'paused') {
      throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
    }
    this.status = 'running';
    if (this.currentStage.status === 'pending') {
      this.currentStage.start();
    }
    this.pauseRequested = false;
  }

  next(): WorkflowWork {
    if (this.status === 'passed') return { kind: 'complete', stage: this.currentStage.stage };
    if (this.status === 'failed') {
      if (!this.failure) {
        throw new WorkflowDomainError('Failed WorkflowRun requires failure details');
      }
      return { kind: 'failed', reason: this.failure };
    }
    if (this.status !== 'running') {
      return {
        kind: 'blocked',
        stage: this.currentStage.stage,
        reason: { complete: false, reason: 'workflow-not-running', stage: this.currentStage.stage },
      };
    }

    const stageRun = this.currentStage;
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    if (stageRun.status === 'failed') {
      if (!stageRun.failure) {
        return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-failed', stage: stageRun.stage } };
      }
      return { kind: 'failed', reason: stageRun.failure };
    }
    if (stageRun.status !== 'running') return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-not-running', stage: stageRun.stage } };

    if (!stageRun.initialized) {
      const source = typeof stageRun.definition.tasksFrom === 'string'
        ? { uses: stageRun.definition.tasksFrom }
        : stageRun.definition.tasksFrom;
      return {
        kind: 'stage-init',
        stage: stageRun.stage,
        definition: {
          tasksFrom: source
            ? {
                uses: source.uses,
                with: source.with,
              }
            : undefined,
        },
      };
    }
    const task = stageRun.currentTask;
    if (task) {
      return {
        kind: 'task',
        stage: stageRun.stage,
        task: {
          id: task.id,
          title: task.title,
          uses: task.uses,
          with: task.withInput,
        },
      };
    }

    const check = stageRun.checks.find(candidate => candidate.status === 'pending');
    if (check) {
      return {
        kind: 'check',
        stage: stageRun.stage,
        check: {
          name: check.name,
          title: check.title,
          uses: check.uses,
          with: check.withInput,
        },
      };
    }

    return { kind: 'complete', stage: stageRun.stage };
  }

  requestPause(): void {
    if (this.status === 'running') {
      this.pauseRequested = true;
    }
  }

  pause(): void {
    if (this.status !== 'running') return;
    this.status = 'paused';
    this.pauseRequested = false;
  }

  initTasks(tasks: MaterializedTaskInput[] = []): void {
    this.currentStageRun().initTasks(tasks);
  }

  failStage(reason: string): void {
    const stageRun = this.currentStageRun();
    const failure = {
      reason: 'task-failed' as const,
      stage: stageRun.stage,
      message: reason,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  completeTask(): void {
    const stageRun = this.requireCurrentTask();
    stageRun.startTask();
    stageRun.completeTask();
  }

  failTask(result: TaskResultInput): void {
    const stageRun = this.requireCurrentTask();
    const taskId = stageRun.currentTask?.id;
    stageRun.startTask();
    stageRun.failTask();
    const failure = {
      reason: 'task-failed' as const,
      stage: stageRun.stage,
      taskId,
      message: result.reason,
      causedBy: result.causedBy,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  passCheck(result: CheckResultInput): void {
    const check = this.requireCurrentCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.pass();
  }

  resetCheck(result: CheckResultInput): void {
    const check = this.requireCurrentCheck();
    check.reset();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
  }

  pendingCheck(result: CheckResultInput): void {
    if (this.isCurrentCheckApproval()) {
      this.requestApproval(result);
      return;
    }
    this.resetCheck(result);
  }

  failCheck(result: CheckResultInput): void {
    const stageRun = this.currentStageRun();
    const check = this.requireCurrentCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.fail();
    const failure = {
      reason: 'check-unrepaired' as const,
      stage: stageRun.stage,
      checkName: check.name,
      message: result.message,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  requestApproval(result: CheckResultInput): void {
    const stageRun = this.currentStageRun();
    const check = this.requireCurrentCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    stageRun.status = 'awaiting-approval';
    stageRun.approval = {
      status: 'awaiting',
      output: result.output ?? null,
      requestedAt: new Date().toISOString(),
      respondedAt: null,
    };
  }

  approve(input: ApprovalInput = {}): void {
    const stageRun = this.currentStageRun();
    if (stageRun.status !== 'awaiting-approval' || stageRun.approval?.status !== 'awaiting') {
      throw new WorkflowDomainError(`Stage ${stageRun.stage} is not awaiting approval`);
    }
    const check = this.requireApprovalCheck();
    check.output = input.output ?? check.output;
    check.pass();
    stageRun.approval = {
      ...stageRun.approval,
      status: 'approved',
      output: input.output ?? stageRun.approval.output,
      respondedAt: new Date().toISOString(),
    };
    stageRun.status = 'running';
  }

  reject(input: ApprovalInput = {}): void {
    const stageRun = this.currentStageRun();
    if (stageRun.status !== 'awaiting-approval' || stageRun.approval?.status !== 'awaiting') {
      throw new WorkflowDomainError(`Stage ${stageRun.stage} is not awaiting approval`);
    }
    const check = this.requireApprovalCheck();
    check.output = input.output ?? check.output;
    check.fail();
    const failure = {
      reason: 'approval-rejected' as const,
      stage: stageRun.stage,
      checkName: check.name,
      message: typeof input.output === 'string' ? input.output : undefined,
    };
    stageRun.approval = {
      ...stageRun.approval,
      status: 'rejected',
      output: input.output ?? stageRun.approval.output,
      respondedAt: new Date().toISOString(),
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  passStage(): boolean {
    const stageRun = this.currentStage;
    if (stageRun.tasks.some(task => task.status !== 'completed')) return false;
    if (stageRun.checks.some(check => check.status !== 'passed')) return false;
    stageRun.status = 'passed';
    const next = this.stageRuns[stageRun.order + 1];
    if (!next) {
      this.status = 'passed';
      return true;
    }
    this.currentStage = next;
    next.start();
    return true;
  }

  private requireCurrentTask(): StageRun {
    const stageRun = this.currentStageRun();
    if (!stageRun.currentTask) {
      throw new WorkflowDomainError(`No current task in stage ${stageRun.stage}`);
    }
    return stageRun;
  }

  private requireCurrentCheck() {
    const stageRun = this.currentStageRun();
    const check = stageRun.checks.find(candidate => candidate.status === 'pending');
    if (!check) throw new WorkflowDomainError(`No current check in stage ${stageRun.stage}`);
    return check;
  }

  private requireApprovalCheck() {
    const stageRun = this.currentStageRun();
    const approvalCheckName = stageRun.definition.approvalCheckName ?? 'user-approval';
    const check = stageRun.checks.find(candidate => candidate.name === approvalCheckName);
    if (!check) throw new WorkflowDomainError(`No approval check in stage ${stageRun.stage}`);
    return check;
  }

  private isCurrentCheckApproval(): boolean {
    const stageRun = this.currentStageRun();
    const check = stageRun.checks.find(candidate => candidate.status === 'pending');
    if (!check || !stageRun.definition.requiresApproval) return false;
    return check.name === (stageRun.definition.approvalCheckName ?? 'user-approval');
  }

  private currentStageRun(): StageRun {
    return this.currentStage;
  }

}
