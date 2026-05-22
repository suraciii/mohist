import type { StageDefinition, WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { ApprovalInput, CheckResult, FailureDetails, MaterializedTaskInput, StageRunState, TaskResult, WorkflowRunStatus, WorkflowWork } from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  currentStage: StageRun;
  pauseRequested = false;

  private _started = false;
  private _paused = false;

  constructor(
    readonly id: string,
    readonly definitionStages: StageDefinition[],
  ) {
    if (definitionStages.length === 0) {
      throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    }
    this.stageRuns = definitionStages.map((def, index) => new StageRun(
      def.stage,
      index,
      def.tasks,
      def.checks,
      def.tasksFrom,
      def.requiresApproval,
    ));
    this.currentStage = this.stageRuns[0];
  }

  get status(): WorkflowRunStatus {
    if (!this._started) return 'pending';
    if (this._paused) return 'paused';
    if (this.currentStage.status === 'failed') return 'failed';
    if (this.currentStage.status === 'passed'
      && this.currentStage === this.stageRuns[this.stageRuns.length - 1]) return 'passed';
    return 'running';
  }

  get failure(): FailureDetails | null {
    return this.currentStage.failure;
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
    this._started = true;
    this._paused = false;
    this.pauseRequested = false;
    if (this.currentStage.status === 'pending') {
      this.currentStage.start();
    }
  }

  next(): WorkflowWork {
    const status = this.status;
    if (status === 'passed') return { kind: 'complete', stage: this.currentStage.stage };
    if (status === 'failed') {
      if (!this.failure) throw new WorkflowDomainError('Failed WorkflowRun requires failure details');
      return { kind: 'failed', reason: this.failure };
    }
    if (status !== 'running') {
      return { kind: 'blocked', stage: this.currentStage.stage, reason: 'workflow-not-running' };
    }

    const work = this.currentStage.nextWork();

    if (work.kind === 'await-approval' && this.currentStage.status === 'running') {
      this.currentStage.requestApproval();
    }

    if (work.kind === 'complete') {
      if (this.passStage()) return { kind: 'complete', stage: this.currentStage.stage };
      return this.next();
    }

    if (work.kind === 'blocked') {
      if (!this.failure) throw new WorkflowDomainError('Failed stage requires failure details');
      return { kind: 'failed', reason: this.failure };
    }

    return { ...work, stage: this.currentStage.stage } as WorkflowWork;
  }

  requestPause(): void {
    if (this.status === 'running') {
      this.pauseRequested = true;
    }
  }

  pause(): void {
    if (this.status !== 'running') return;
    this._paused = true;
    this.pauseRequested = false;
  }

  initTasks(tasks: MaterializedTaskInput[] = []): void {
    this.currentStage.initTasks(tasks);
  }

  failStage(reason: string): void {
    this.currentStage.failure = {
      reason: 'task-failed',
      stage: this.currentStage.stage,
      message: reason,
    };
  }

  completeTask(): void {
    this.currentStage.completeTask();
  }

  failTask(result: TaskResult): void {
    this.currentStage.failTask(result.reason);
  }

  passCheck(result: CheckResult): void {
    this.currentStage.passCheck(result);
  }

  resetCheck(result: CheckResult): void {
    this.currentStage.resetCheck(result);
  }

  pendingCheck(result: CheckResult): void {
    this.currentStage.resetCheck(result);
  }

  failCheck(result: CheckResult): void {
    this.currentStage.failCheck(result);
  }

  approve(input: ApprovalInput = {}): void {
    this.currentStage.approve(input);
  }

  reject(input: ApprovalInput = {}): void {
    this.currentStage.reject(input);
  }

  passStage(): boolean {
    if (!this.currentStage.isComplete) return false;
    if (this.currentStage.requiresApproval && this.currentStage.approval?.status !== 'approved') return false;
    const next = this.stageRuns[this.currentStage.order + 1];
    if (!next) return true;
    this.currentStage = next;
    next.start();
    return false;
  }
}
