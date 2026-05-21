import type { WorkflowDefinitionSnapshot } from '../workflow-definition-snapshot';
import type { CheckDefinition, TaskDefinition, WorkflowStageId, WorkflowTasksFromSource } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { CheckResultInput, FailureDetails, MaterializedTaskInput, StageRunState, TaskResultInput, WorkflowRunStatus, WorkflowWork } from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'pending';
  currentStage: WorkflowStageId;
  failure: FailureDetails | null = null;

  constructor(
    readonly id: string,
    readonly definitionSnapshot: WorkflowDefinitionSnapshot,
  ) {
    if (definitionSnapshot.stages.length === 0) {
      throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    }
    this.stageRuns = definitionSnapshot.stages.map((definition, index) => new StageRun(definition, index));
    this.currentStage = definitionSnapshot.stages[0].stage;
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
      tasks: stageRun.tasks.map((taskRun, index) => {
        const task = stageRun.definition.tasks[index];
        return {
          id: task?.id ?? `task-${index}`,
          title: task?.title ?? `Task ${index + 1}`,
          uses: task?.uses,
          status: taskRun.status,
        };
      }),
      checks: stageRun.checks.map(check => ({
        name: check.name,
        title: check.title,
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
    if (this.status === 'passed') return { kind: 'complete', stage: this.currentStage };
    if (this.status === 'failed') {
      if (!this.failure) {
        throw new WorkflowDomainError('Failed WorkflowRun requires failure details');
      }
      return { kind: 'failed', reason: this.failure };
    }
    if (this.status !== 'running') return { kind: 'blocked', stage: this.currentStage, reason: { complete: false, reason: 'workflow-not-running', stage: this.currentStage } };

    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return { kind: 'blocked', stage: this.currentStage, reason: { complete: false, reason: 'missing-current-stage', stage: this.currentStage } };
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    if (stageRun.status === 'failed') {
      if (!stageRun.failure) {
        return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-failed', stage: stageRun.stage } };
      }
      return { kind: 'failed', reason: stageRun.failure };
    }
    if (stageRun.status !== 'running') return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-not-running', stage: stageRun.stage } };

    if (stageRun.definition.tasksFrom && !stageRun.workSourceState.evaluated) {
      return { kind: 'task-source', stage: stageRun.stage };
    }

    const taskDefinition = stageRun.currentTaskDefinition;
    if (taskDefinition) return { kind: 'task', stage: stageRun.stage, taskId: taskDefinition.id };

    const check = stageRun.checks.find(candidate => candidate.status === 'pending');
    if (check) return { kind: 'check', stage: stageRun.stage, checkName: check.name };

    return { kind: 'complete', stage: stageRun.stage };
  }

  taskSourceDefinition(stage: WorkflowStageId): WorkflowTasksFromSource | null {
    return this.stageRun(stage).definition.tasksFrom ?? null;
  }

  taskDefinition(stage: WorkflowStageId, taskId: string): TaskDefinition | null {
    return this.stageRun(stage).definition.tasks.find(candidate => candidate.id === taskId) ?? null;
  }

  checkDefinition(stage: WorkflowStageId, checkName: string): CheckDefinition | null {
    return this.stageRun(stage).definition.checks.find(candidate => candidate.name === checkName) ?? null;
  }

  completeTaskSource(stage: WorkflowStageId, tasks: MaterializedTaskInput[]): void {
    const stageRun = this.stageRun(stage);
    stageRun.workSourceState = tasks.length === 0
      ? { evaluated: true, empty: true }
      : { evaluated: true, tasks };
    for (const task of tasks) {
      stageRun.addTask(task.id, task.title, task.uses);
    }
  }

  missTaskSource(stage: WorkflowStageId): void {
    this.stageRun(stage).workSourceState = { evaluated: true, missing: true };
  }

  failTaskSource(stage: WorkflowStageId): void {
    this.stageRun(stage).workSourceState = { evaluated: true, invalid: true };
  }

  emptyTaskSource(stage: WorkflowStageId): void {
    this.stageRun(stage).workSourceState = { evaluated: true, empty: true };
  }

  completeTask(stage: WorkflowStageId, taskId: string): void {
    const stageRun = this.requireCurrentTask(stage, taskId);
    stageRun.startTask();
    stageRun.completeTask();
  }

  failTask(stage: WorkflowStageId, taskId: string, result: TaskResultInput): void {
    const stageRun = this.requireCurrentTask(stage, taskId);
    stageRun.startTask();
    stageRun.failTask();
    const failure = {
      reason: 'task-failed' as const,
      stage,
      taskId,
      message: result.reason,
      causedBy: result.causedBy,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  passCheck(stage: WorkflowStageId, checkName: string, result: CheckResultInput): void {
    const check = this.requireCheck(stage, checkName);
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.pass();
  }

  resetCheck(stage: WorkflowStageId, checkName: string, result: CheckResultInput): void {
    const check = this.requireCheck(stage, checkName);
    check.reset();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
  }

  failCheck(stage: WorkflowStageId, checkName: string, result: CheckResultInput): void {
    const stageRun = this.stageRun(stage);
    const check = this.requireCheck(stage, checkName);
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.fail();
    const failure = {
      reason: 'check-unrepaired' as const,
      stage,
      checkName,
      message: result.message,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  passStage(): boolean {
    const stageRun = this.stageRun(this.currentStage);
    if (stageRun.tasks.some(task => task.status !== 'completed')) return false;
    if (stageRun.checks.some(check => check.status !== 'passed')) return false;
    stageRun.status = 'passed';
    const next = this.stageRuns[stageRun.order + 1];
    if (!next) {
      this.status = 'passed';
      return true;
    }
    this.currentStage = next.stage;
    next.start();
    return true;
  }

  private requireCurrentTask(stage: WorkflowStageId, taskId: string): StageRun {
    const stageRun = this.stageRun(stage);
    const definition = stageRun.currentTaskDefinition;
    if (!definition || definition.id !== taskId) {
      throw new WorkflowDomainError(`Task ${taskId} is not current task in stage ${stage}`);
    }
    return stageRun;
  }

  private requireCheck(stage: WorkflowStageId, checkName: string) {
    const stageRun = this.stageRun(stage);
    const check = stageRun.checks.find(candidate => candidate.name === checkName);
    if (!check) throw new WorkflowDomainError(`Check ${checkName} does not exist in stage ${stage}`);
    return check;
  }

  private stageRun(stage: WorkflowStageId): StageRun {
    const stageRun = this.stageRuns.find(candidate => candidate.stage === stage);
    if (!stageRun) throw new WorkflowDomainError(`Stage ${stage} is not admitted by this workflow`);
    return stageRun;
  }
}
