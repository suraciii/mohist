import type { WorkflowDefinitionSnapshot } from '../workflow-definition-snapshot';
import type { WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { CheckResultInput, FailureDetails, MaterializedTaskInput, StageRunState, TaskResultInput, WorkflowRunStatus, WorkflowWork } from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'pending';
  currentStage: StageRun;
  failure: FailureDetails | null = null;

  constructor(
    readonly id: string,
    readonly definitionSnapshot: WorkflowDefinitionSnapshot,
  ) {
    if (definitionSnapshot.stages.length === 0) {
      throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    }
    this.stageRuns = definitionSnapshot.stages.map((definition, index) => new StageRun(definition, index));
    this.currentStage = this.stageRuns[0];
  }

  get stageOrder(): WorkflowStageId[] {
    return this.definitionSnapshot.stages.map(definition => definition.stage);
  }

  get stages(): StageRunState[] {
    return this.stageRuns.map(stageRun => ({
      stage: stageRun.stage,
      status: stageRun.status,
      order: stageRun.order,
      attemptSequence: stageRun.attemptSequence,
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
      commitPoint: stageRun.commitPoint,
      workSourceState: stageRun.workSourceState,
    }));
  }

  start(): void {
    if (this.status !== 'pending') {
      throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
    }
    this.status = 'running';
    this.stageRuns[0].start();
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

    if (stageRun.definition.tasksFrom && !stageRun.workSourceState.evaluated) {
      const source = typeof stageRun.definition.tasksFrom === 'string'
        ? { uses: stageRun.definition.tasksFrom }
        : stageRun.definition.tasksFrom;
      return {
        kind: 'task-source',
        stage: stageRun.stage,
        definition: {
          uses: source.uses,
          with: source.with,
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

  addTasks(tasks: MaterializedTaskInput[]): void {
    const stageRun = this.currentStageRun();
    stageRun.workSourceState = tasks.length === 0
      ? { evaluated: true, empty: true }
      : { evaluated: true, tasks };
    for (const task of tasks) {
      stageRun.addTask(task.id, task.title, task.uses);
    }
  }

  markTaskSourceMissing(): void {
    this.currentStageRun().workSourceState = { evaluated: true, missing: true };
  }

  markTaskSourceInvalid(): void {
    this.currentStageRun().workSourceState = { evaluated: true, invalid: true };
  }

  markTaskSourceEmpty(): void {
    this.currentStageRun().workSourceState = { evaluated: true, empty: true };
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

  private currentStageRun(): StageRun {
    return this.currentStage;
  }

}
