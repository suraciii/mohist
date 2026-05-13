import { WorkflowRunRepo } from '../db/workflow-run-repo';
import { DatabaseManager } from '../db/database';
import { Stage } from '../types';
import {
  type ApprovalInput,
  type CheckResultInput,
  type MaterializedTaskInput,
  type TaskResultInput,
  type WorkflowDecision,
  type WorkflowRun,
  type WorkflowWork,
} from '../workflow/domain';
import { WorkflowRunProjection } from './workflow-run-projection';

export interface WorkflowRunRepositoryPort {
  createOrLoadActiveAggregate(data: { issueId: string; issueNumber: number; startedBy?: string | null; tasksPath?: string }): WorkflowRun;
  loadActiveAggregate(issueId: string, options?: { tasksPath?: string }): WorkflowRun | null;
  loadRunningAggregate?(issueId: string, options?: { tasksPath?: string }): WorkflowRun | null;
  saveAggregate(run: WorkflowRun, startedBy?: string | null): void;
}

export interface WorkflowRunProjectionPort {
  apply(input: { run: WorkflowRun; decision: WorkflowDecision; sessionId?: string | null }): void;
}

export interface WorkflowCommandOptions {
  tasksPath?: string;
  sessionId?: string | null;
  startedBy?: string | null;
}

export class WorkflowApplicationService {
  private repo: WorkflowRunRepositoryPort;
  private projection: WorkflowRunProjectionPort;

  constructor(db: DatabaseManager);
  constructor(repo: WorkflowRunRepositoryPort, projection: WorkflowRunProjectionPort);
  constructor(dbOrRepo: DatabaseManager | WorkflowRunRepositoryPort, projection?: WorkflowRunProjectionPort) {
    if (projection) {
      this.repo = dbOrRepo as WorkflowRunRepositoryPort;
      this.projection = projection;
    } else {
      const db = dbOrRepo as DatabaseManager;
      this.repo = new WorkflowRunRepo(db);
      this.projection = new WorkflowRunProjection(db);
    }
  }

  startWorkflow(input: { issueId: string; issueNumber: number } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.repo.createOrLoadActiveAggregate(input);
    const decision = this.decisionForProjection(run, [{ type: 'workflow-started', stage: run.currentStage }, { type: 'stage-started', stage: run.currentStage }]);
    this.projection.apply({ run, decision, sessionId: input.sessionId });
    return { run, decision };
  }

  materializeTasks(input: { issueId: string; stage: Stage; tasks: MaterializedTaskInput[] } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.materializeTasks(input.stage, input.tasks));
  }

  completeTask(input: { issueId: string; stage: Stage; taskId: string; result: TaskResultInput } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.completeTask(input.stage, input.taskId, input.result));
  }

  recordCheckResult(input: { issueId: string; stage: Stage; result: CheckResultInput } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.recordCheckResult(input.stage, input.result));
  }

  approveStage(input: { issueId: string; stage: Stage; approval?: ApprovalInput } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.approveStage(input.stage, input.approval));
  }

  rejectStage(input: { issueId: string; stage: Stage; approval?: ApprovalInput } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.rejectStage(input.stage, input.approval));
  }

  resumeDecision(issueId: string, options: WorkflowCommandOptions = {}): { run: WorkflowRun; nextWork: WorkflowWork } {
    const run = this.loadActive(issueId, options.tasksPath);
    return { run, nextWork: run.nextWork() };
  }

  private updateActiveRun(
    issueId: string,
    options: WorkflowCommandOptions,
    decide: (run: WorkflowRun) => WorkflowDecision,
  ): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.loadActive(issueId, options.tasksPath);
    const decision = decide(run);
    this.repo.saveAggregate(run, options.startedBy ?? null);
    this.projection.apply({ run, decision, sessionId: options.sessionId });
    return { run, decision };
  }

  private loadActive(issueId: string, tasksPath?: string): WorkflowRun {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(issueId, { tasksPath })
      : this.repo.loadActiveAggregate(issueId, { tasksPath });
    if (!run) throw new Error(`No active WorkflowRun for issue ${issueId}`);
    return run;
  }

  private decisionForProjection(run: WorkflowRun, events: WorkflowDecision['events']): WorkflowDecision {
    return { events, nextWork: run.nextWork() };
  }
}
