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
  loadLatestAggregate?(issueId: string, options?: { tasksPath?: string }): WorkflowRun | null;
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

type CheckRepairScheduleStatus = 'scheduled' | 'already-running' | 'exhausted' | 'not-check-stage' | 'not-available';

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

  retryStage(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.repo.loadLatestAggregate
      ? this.repo.loadLatestAggregate(input.issueId, { tasksPath: input.tasksPath })
      : this.repo.loadActiveAggregate(input.issueId, { tasksPath: input.tasksPath });
    if (!run) throw new Error(`No WorkflowRun for issue ${input.issueId}`);
    const decision = run.retryStage(input.stage);
    this.repo.saveAggregate(run, input.startedBy ?? null);
    this.projection.apply({ run, decision, sessionId: input.sessionId });
    return { run, decision };
  }

  rerunStage(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(input.issueId, { tasksPath: input.tasksPath })
      : this.repo.loadActiveAggregate(input.issueId, { tasksPath: input.tasksPath });
    if (run) {
      const decision = run.rerunStage(input.stage);
      this.repo.saveAggregate(run, input.startedBy ?? null);
      this.projection.apply({ run, decision, sessionId: input.sessionId });
      return { run, decision };
    }

    const latestRun = this.repo.loadLatestAggregate?.(input.issueId, { tasksPath: input.tasksPath }) ?? null;
    if (latestRun?.snapshot().status !== 'failed' || latestRun.currentStage !== input.stage) {
      throw new Error(`No active WorkflowRun for issue ${input.issueId}`);
    }

    const decision = latestRun.retryStage(input.stage);
    this.repo.saveAggregate(latestRun, input.startedBy ?? null);
    this.projection.apply({ run: latestRun, decision, sessionId: input.sessionId });
    return { run: latestRun, decision };
  }

  scheduleRebaseTask(input: { issueId: string; reason?: string } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.scheduleRebaseTask(input.reason));
  }

  scheduleFixReviewFindings(input: { issueId: string; stage: Stage.Check } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    const run = this.repo.loadActiveAggregate(input.issueId, { tasksPath: input.tasksPath });
    if (!run) {
      const latestRun = this.repo.loadLatestAggregate?.(input.issueId, { tasksPath: input.tasksPath });
      if (!latestRun) throw new Error(`No WorkflowRun for issue ${input.issueId}`);
      if (latestRun.snapshot().status !== 'failed') throw new Error(`No active or failed WorkflowRun for issue ${input.issueId}`);
      return this.handleFixReviewFindingsOnFailedRun(latestRun, input);
    }
    return this.handleFixReviewFindingsOnRunningRun(run, input);
  }

  private handleFixReviewFindingsOnRunningRun(run: WorkflowRun, input: WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    if (run.currentStage !== Stage.Check) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }
    return this.doScheduleFixReviewFindings(run, input);
  }

  private handleFixReviewFindingsOnFailedRun(run: WorkflowRun, input: WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    if (run.currentStage !== Stage.Check) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }
    return this.doScheduleFixReviewFindings(run, input);
  }

  private doScheduleFixReviewFindings(run: WorkflowRun, input: WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    const checkStageRun = run.stageRun(Stage.Check);
    const policy = checkStageRun.definition.checkFailurePolicies?.find(p => p.checkName === 'review-passed');
    if (!policy) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }

    const reviewPassedCheck = checkStageRun.checks.find(c => c.name === 'review-passed');
    const existingPendingFix = checkStageRun.tasks.find(t =>
      (t.id === 'fix-review-findings' || t.id.startsWith('fix-review-findings:')) &&
      (t.status === 'pending' || t.status === 'running')
    );
    if (existingPendingFix) {
      run.status = 'running';
      run.failure = null;
      checkStageRun.reopenForRepair();
      this.repo.saveAggregate(run, input.startedBy ?? null);
      this.projection.apply({ run, decision: { events: [], nextWork: run.nextWork() }, sessionId: input.sessionId });
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: existingPendingFix.id, repairStatus: 'already-running' };
    }

    const scheduledFixCount = checkStageRun.scheduledFixCount('review-passed');
    if (scheduledFixCount >= policy.maxAttempts) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'exhausted' };
    }

    if (reviewPassedCheck?.status !== 'failed') {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-available' };
    }

    const causedBy = {
      type: 'check-failure' as const,
      checkName: 'review-passed',
      message: reviewPassedCheck?.message ?? 'Review failed',
    };
    const fixTask = checkStageRun.appendFixTask(policy, causedBy);
    run.status = 'running';
    run.failure = null;
    checkStageRun.reopenForRepair();
    const events: import('../workflow/domain').WorkflowEvent[] = [
      { type: 'fix-task-scheduled', stage: Stage.Check, taskId: fixTask.id, causedBy },
    ];
    this.repo.saveAggregate(run, input.startedBy ?? null);
    this.projection.apply({ run, decision: { events, nextWork: run.nextWork() }, sessionId: input.sessionId });
    return { run, decision: { events, nextWork: run.nextWork() }, repairTaskId: fixTask.id, repairStatus: 'scheduled' };
  }

  resumeDecision(issueId: string, options: WorkflowCommandOptions = {}): { run: WorkflowRun; nextWork: WorkflowWork } {
    const run = this.loadForDecision(issueId, options.tasksPath);
    const nextWork = run.nextWork();
    if (nextWork.kind === 'failed') {
      this.repo.saveAggregate(run, options.startedBy ?? null);
      this.projection.apply({ run, decision: { events: [], nextWork }, sessionId: options.sessionId });
    }
    return { run, nextWork };
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

  private loadForDecision(issueId: string, tasksPath?: string): WorkflowRun {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(issueId, { tasksPath })
      : this.repo.loadActiveAggregate(issueId, { tasksPath });
    if (run) return run;

    const latestRun = this.repo.loadLatestAggregate?.(issueId, { tasksPath }) ?? null;
    if (latestRun?.snapshot().status === 'failed') return latestRun;
    throw new Error(`No active WorkflowRun for issue ${issueId}`);
  }

  private decisionForProjection(run: WorkflowRun, events: WorkflowDecision['events']): WorkflowDecision {
    return { events, nextWork: run.nextWork() };
  }
}
