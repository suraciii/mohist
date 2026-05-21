import type { WorkflowStageId } from '../model';
import { WorkflowRun } from '../model';
import type {
  WorkflowRunId,
  WorkflowRunResult,
  WorkflowRunner as WorkflowRunnerContract,
  WorkflowStore,
  WorkflowRunStatus,
  WorkflowStageState,
  WorkflowFailure,
} from './types';

export class WorkflowRunner implements WorkflowRunnerContract {
  constructor(
    private readonly run: WorkflowRun,
    private readonly store: WorkflowStore,
  ) {}

  get id(): WorkflowRunId {
    return this.run.id;
  }

  get status(): WorkflowRunStatus {
    const status = this.run.status;
    return status === 'passed' ? 'completed' : status;
  }

  get currentStage(): WorkflowStageId {
    return this.run.currentStage;
  }

  get stages(): WorkflowStageState[] {
    return this.run.stageRuns.map(stageRun => ({
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

  get failure(): WorkflowFailure | null {
    return this.run.failure;
  }

  async start(): Promise<WorkflowRunResult> {
    if (this.run.status === 'pending') {
      this.run.start();
    }
    await this.persist();
    return this.resultFromRun();
  }

  async resume(): Promise<WorkflowRunResult> {
    await this.persist();
    return this.resultFromRun();
  }

  async pause(reason?: string): Promise<WorkflowRunResult> {
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: reason ?? 'Workflow paused',
    });
  }

  async approve(): Promise<WorkflowRunResult> {
    await this.persist();
    return this.resultFromRun();
  }

  async reject(reason?: string): Promise<WorkflowRunResult> {
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: reason,
    });
  }

  async persist(): Promise<void> {
    await this.store.save(this.run);
  }

  private result(result: WorkflowRunResult): WorkflowRunResult {
    return result;
  }

  private resultFromRun(): WorkflowRunResult {
    if (this.run.status === 'passed') return { status: 'completed', stage: this.run.currentStage };
    if (this.run.status === 'failed') {
      return {
        status: 'failed',
        stage: this.run.currentStage,
        message: this.run.failure?.message ?? this.run.failure?.reason,
      };
    }
    if (this.run.status === 'cancelled') return { status: 'stopped', stage: this.run.currentStage };
    return { status: 'running', stage: this.run.currentStage };
  }
}
