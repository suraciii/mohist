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
  type WorkflowRecoverySummary,
  type WorkflowDefinitionSnapshot,
  type WorkItemAttemptState,
} from '../workflow/model';
import { WorkflowRunProjection } from './workflow-run-projection';
import type { WorkflowAttemptEvidencePort, AttemptReconciliationResult } from './attempt-reconciliation-service';
import { AttemptReconciliationService } from './attempt-reconciliation-service';

export interface WorkflowRunRepositoryPort {
  createOrLoadActiveAggregate(data: { issueId: string; issueNumber: number; startedBy?: string | null; workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot }): WorkflowRun;
  loadActiveAggregate(issueId: string): WorkflowRun | null;
  loadRunningAggregate?(issueId: string): WorkflowRun | null;
  loadLatestAggregate?(issueId: string): WorkflowRun | null;
  saveAggregate(run: WorkflowRun, startedBy?: string | null): void;
}

export interface WorkflowRunProjectionPort {
  apply(input: { run: WorkflowRun; decision: WorkflowDecision; sessionId?: string | null }): void;
}

export interface WorkflowCommandOptions {
  sessionId?: string | null;
  startedBy?: string | null;
  workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot;
  workSourceState?: 'missing' | 'invalid' | 'empty';
}

type CheckRepairScheduleStatus = 'scheduled' | 'already-running' | 'exhausted' | 'not-check-stage' | 'not-available';

export type RetryRejectionReason =
  | 'no-failed-workflow-run'
  | 'stage-mismatch'
  | 'no-retryable-failed-work'
  | 'missing-project'
  | 'missing-worktree'
  | 'missing-change-artifacts'
  | 'latest-attempt-interrupted'
  | 'latest-attempt-running';

export interface RecoveryProjection {
  currentWorkItem: {
    type: 'task' | 'check';
    id: string;
    title: string;
  } | null;
  latestAttemptState: WorkItemAttemptState | null;
  workflowSummaryState: WorkflowRecoverySummary | null;
  allowedActions: string[];
}

export interface RetryAvailability {
  available: true;
  reason: null;
}

export interface RetryRejection {
  available: false;
  reason: RetryRejectionReason;
  message: string;
}

export type RetryCheckResult = RetryAvailability | RetryRejection;

export class WorkflowApplicationService {
  private repo: WorkflowRunRepositoryPort;
  private projection: WorkflowRunProjectionPort;
  private reconciliationService: AttemptReconciliationService | null;

  constructor(db: DatabaseManager);
  constructor(repo: WorkflowRunRepositoryPort, projection: WorkflowRunProjectionPort);
  constructor(dbOrRepo: DatabaseManager | WorkflowRunRepositoryPort, projection?: WorkflowRunProjectionPort) {
    if (projection) {
      this.repo = dbOrRepo as WorkflowRunRepositoryPort;
      this.projection = projection;
      this.reconciliationService = null;
    } else {
      const db = dbOrRepo as DatabaseManager;
      this.repo = new WorkflowRunRepo(db);
      this.projection = new WorkflowRunProjection(db);
      this.reconciliationService = AttemptReconciliationService.fromDatabase(db);
    }
  }

  setEvidencePort(evidencePort: WorkflowAttemptEvidencePort): void {
    this.reconciliationService = new AttemptReconciliationService(evidencePort);
  }

  reconcileIssueWorkflow(issueId: string, options: WorkflowCommandOptions = {}): AttemptReconciliationResult {
    const run = this.loadAggregateForReconciliation(issueId);
    if (!run) {
      return { reconciled: false, interruptedCount: 0, reasons: [], interruptedAttempts: [] };
    }

    const stageRun = run.currentStageRun();
    if (!stageRun) {
      return { reconciled: false, interruptedCount: 0, reasons: [], interruptedAttempts: [] };
    }

    const runningAttempts: import('../workflow/model').WorkItemAttempt[] = [];
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running') {
        runningAttempts.push(task.latestAttempt);
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') {
        runningAttempts.push(check.latestAttempt);
      }
    }

    if (runningAttempts.length === 0) {
      return { reconciled: false, interruptedCount: 0, reasons: [], interruptedAttempts: [] };
    }

    if (!this.reconciliationService) {
      return { reconciled: false, interruptedCount: 0, reasons: [], interruptedAttempts: [] };
    }

    const result = this.reconciliationService.reconcileRunningAttempts(issueId, runningAttempts);

    if (result.reconciled) {
      run.interruptSpecificWorkAttempts(result.interruptedAttempts, 'agent-lost', 'reconciliation: no live execution evidence');
      this.repo.saveAggregate(run);
      const decision: WorkflowDecision = { events: [], nextWork: run.nextWork() };
      this.projection.apply({ run, decision, sessionId: options.sessionId });
    }

    return result;
  }

  private loadAggregateForReconciliation(issueId: string): WorkflowRun | null {
    const running = this.repo.loadRunningAggregate?.(issueId)
      ?? this.repo.loadActiveAggregate(issueId);
    if (running) return running;
    return this.repo.loadLatestAggregate?.(issueId) ?? null;
  }

  getWorkflowRecoverySummary(issueId: string, options: WorkflowCommandOptions = {}): WorkflowRecoverySummary | null {
    this.reconcileIssueWorkflow(issueId, options);
    const run = this.repo.loadLatestAggregate?.(issueId)
      ?? this.repo.loadActiveAggregate(issueId);
    if (!run) return null;
    return run.workflowRecoverySummary();
  }

  getRecoveryProjection(issueId: string, options: WorkflowCommandOptions = {}): RecoveryProjection | null {
    this.reconcileIssueWorkflow(issueId, options);
    const run = this.repo.loadLatestAggregate?.(issueId)
      ?? this.repo.loadActiveAggregate(issueId);
    if (!run) return null;

    let workflowSummaryState = run.workflowRecoverySummary();
    const stageRun = run.currentStageRun();

    if (!stageRun) {
      return {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState,
        allowedActions: this.computeAllowedActions(null, workflowSummaryState),
      };
    }

    const blocking = this.findCurrentWorkItem(stageRun);
    if (!blocking && workflowSummaryState === 'running') {
      const nextWork = run.nextWork();
      if (nextWork.kind === 'blocked') {
        workflowSummaryState = 'waiting-for-recovery';
      }
    }
    return {
      currentWorkItem: blocking ? { type: blocking.type, id: blocking.id, title: blocking.title } : null,
      latestAttemptState: blocking?.attemptState ?? null,
      workflowSummaryState,
      allowedActions: this.computeAllowedActions(blocking?.attemptState ?? null, workflowSummaryState),
    };
  }

  private findCurrentWorkItem(stageRun: import('../workflow/model').StageRun): {
    type: 'task' | 'check';
    id: string;
    title: string;
    attemptState: WorkItemAttemptState | null;
  } | null {
    const failedTask = stageRun.tasks.find(task => task.status === 'failed' || task.status === 'skipped');
    if (failedTask) return {
      type: 'task',
      id: failedTask.id,
      title: failedTask.title,
      attemptState: failedTask.latestAttempt?.state ?? null,
    };
    const failedCheck = stageRun.checks.find(check => check.status === 'failed' || check.status === 'error');
    if (failedCheck) return {
      type: 'check',
      id: failedCheck.name,
      title: failedCheck.title,
      attemptState: failedCheck.latestAttempt?.state ?? null,
    };
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) return {
      type: 'check',
      id: preTaskCheck.name,
      title: preTaskCheck.title,
      attemptState: preTaskCheck.latestAttempt?.state ?? null,
    };
    const task = stageRun.nextTask();
    if (task) return { type: 'task', id: task.id, title: task.title, attemptState: task.latestAttempt?.state ?? null };
    const postTaskCheck = stageRun.nextCheck('post-task');
    if (postTaskCheck) return {
      type: 'check',
      id: postTaskCheck.name,
      title: postTaskCheck.title,
      attemptState: postTaskCheck.latestAttempt?.state ?? null,
    };
    const blocking = this.findBlockingAttemptWorkItem(stageRun);
    if (blocking) return blocking;
    return null;
  }

  private findBlockingAttemptWorkItem(stageRun: import('../workflow/model').StageRun): {
    type: 'task' | 'check';
    id: string;
    title: string;
    attemptState: WorkItemAttemptState;
  } | null {
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running') {
        return { type: 'task', id: task.id, title: task.title, attemptState: 'running' };
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') {
        return { type: 'check', id: check.name, title: check.title, attemptState: 'running' };
      }
    }
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'interrupted') {
        return { type: 'task', id: task.id, title: task.title, attemptState: 'interrupted' };
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'interrupted') {
        return { type: 'check', id: check.name, title: check.title, attemptState: 'interrupted' };
      }
    }
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'failed') {
        return { type: 'task', id: task.id, title: task.title, attemptState: 'failed' };
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'failed') {
        return { type: 'check', id: check.name, title: check.title, attemptState: 'failed' };
      }
    }
    return null;
  }

  private computeAllowedActions(attemptState: WorkItemAttemptState | null, workflowSummary: WorkflowRecoverySummary): string[] {
    if (attemptState === 'running') return ['wait', 'stop'];
    if (attemptState === 'failed') return ['retry', 'rerun', 'inspect'];
    if (attemptState === 'interrupted') return ['resume', 'rerun', 'inspect'];
    if (attemptState === 'completed') return [];
    if (workflowSummary === 'awaiting-approval') return ['approve', 'reject'];
    if (workflowSummary === 'completed') return [];
    if (workflowSummary === 'waiting-for-recovery') return ['rerun', 'inspect'];
    return [];
  }

  checkRetryAvailability(input: { issueId: string; stage: Stage }): RetryCheckResult {
    this.reconcileIssueWorkflow(input.issueId);

    const run = this.repo.loadLatestAggregate?.(input.issueId)
      ?? this.repo.loadActiveAggregate(input.issueId);

    if (!run) {
      return {
        available: false as const,
        reason: 'no-failed-workflow-run' as const,
        message: `No workflow run found for this issue. The pipeline may not have started yet.`,
      };
    }

    if (run.currentStage !== input.stage) {
      return {
        available: false as const,
        reason: 'stage-mismatch' as const,
        message: `Workflow run is in '${run.currentStage}' stage, but this action is for '${input.stage}'.`,
      };
    }

    const stageRun = run.stageRun(input.stage);
    if (!stageRun) {
      return {
        available: false as const,
        reason: 'stage-mismatch' as const,
        message: `No stage run found for '${input.stage}'.`,
      };
    }

    const currentWork = this.findCurrentWorkItem(stageRun);
    if (currentWork?.attemptState === 'running') {
      return {
        available: false as const,
        reason: 'latest-attempt-running' as const,
        message: `Cannot retry: ${currentWork.type} '${currentWork.id}' is still running. Wait for it to complete or stop it first.`,
      };
    }
    if (currentWork?.attemptState === 'interrupted') {
      return {
        available: false as const,
        reason: 'latest-attempt-interrupted' as const,
        message: `Cannot retry: ${currentWork.type} '${currentWork.id}' was interrupted, not failed. Interrupted work is not retryable as failed work. Use resume, rerun stage, or inspect instead.`,
      };
    }
    if (currentWork?.attemptState === 'failed') {
      return { available: true as const, reason: null };
    }

    return {
      available: false as const,
      reason: 'no-retryable-failed-work' as const,
      message: `No failed latest attempt was found for the current work item in the '${input.stage}' stage.`,
    };
  }

  startWorkflow(input: { issueId: string; issueNumber: number } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.repo.createOrLoadActiveAggregate(input);
    const decision = this.decisionForProjection(run, [{ type: 'workflow-started', stage: run.currentStage }, { type: 'stage-started', stage: run.currentStage }]);
    this.projection.apply({ run, decision, sessionId: input.sessionId });
    return { run, decision };
  }

  materializeTasks(input: { issueId: string; stage: Stage; tasks: MaterializedTaskInput[]; workSourceState?: 'missing' | 'invalid' | 'empty' } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.materializeTasks(input.stage, input.tasks, input.workSourceState));
  }

  completeTask(input: { issueId: string; stage: Stage; taskId: string; result: TaskResultInput } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.completeTask(input.stage, input.taskId, input.result));
  }

  startTaskAttempt(input: { issueId: string; stage: Stage; taskId: string; evidence?: Partial<Pick<import('../workflow/model').WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> }): void {
    const run = this.loadActive(input.issueId);
    run.startTaskAttempt(input.stage, input.taskId, new Date().toISOString(), input.evidence);
    this.repo.saveAggregate(run);
  }

  startCheckAttempt(input: { issueId: string; stage: Stage; checkName: string; evidence?: Partial<Pick<import('../workflow/model').WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> }): void {
    const run = this.loadActive(input.issueId);
    run.startCheckAttempt(input.stage, input.checkName, new Date().toISOString(), input.evidence);
    this.repo.saveAggregate(run);
  }

  interruptRunningWorkAttempts(input: { issueId: string; reason: string; diagnostic?: string | null }): void {
    const run = this.repo.loadActiveAggregate(input.issueId);
    if (!run) return;
    run.interruptRunningWorkAttempts(input.reason, input.diagnostic ?? null);
    this.repo.saveAggregate(run);
    const decision: WorkflowDecision = { events: [], nextWork: run.nextWork() };
    this.projection.apply({ run, decision });
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
      ? this.repo.loadLatestAggregate(input.issueId)
      : this.repo.loadActiveAggregate(input.issueId);
    if (!run) throw new Error(`No WorkflowRun for issue ${input.issueId}`);
    const decision = run.retryStage(input.stage);
    this.repo.saveAggregate(run, input.startedBy ?? null);
    this.projection.apply({ run, decision, sessionId: input.sessionId });
    return { run, decision };
  }

  retryStageOrReject(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { ok: true; run: WorkflowRun; decision: WorkflowDecision } | { ok: false; reason: RetryRejectionReason; message: string } {
    this.reconcileIssueWorkflow(input.issueId, input);
    const availability = this.checkRetryAvailability(input);
    if (!availability.available) {
      return { ok: false, reason: availability.reason, message: availability.message };
    }

    const run = this.repo.loadLatestAggregate
      ? this.repo.loadLatestAggregate(input.issueId)
      : this.repo.loadActiveAggregate(input.issueId);
    if (!run) {
      return {
        ok: false,
        reason: 'no-failed-workflow-run' as const,
        message: `No workflow run found for this issue.`,
      };
    }

    try {
      const decision = run.retryStage(input.stage);
      this.repo.saveAggregate(run, input.startedBy ?? null);
      this.projection.apply({ run, decision, sessionId: input.sessionId });
      return { ok: true, run, decision };
    } catch (error) {
      if (error instanceof Error) {
        return {
          ok: false,
          reason: 'no-retryable-failed-work' as const,
          message: `Retry failed: ${error.message}`,
        };
      }
      throw error;
    }
  }

  rerunStage(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(input.issueId)
      : this.repo.loadActiveAggregate(input.issueId);
    if (run) {
      const decision = run.rerunStage(input.stage);
      this.repo.saveAggregate(run, input.startedBy ?? null);
      this.projection.apply({ run, decision, sessionId: input.sessionId });
      return { run, decision };
    }

    const latestRun = this.repo.loadLatestAggregate?.(input.issueId) ?? null;
    if (latestRun?.snapshot().status !== 'failed' || latestRun.currentStage !== input.stage) {
      throw new Error(`No active WorkflowRun for issue ${input.issueId}`);
    }

    const decision = latestRun.rerunStage(input.stage);
    this.repo.saveAggregate(latestRun, input.startedBy ?? null);
    this.projection.apply({ run: latestRun, decision, sessionId: input.sessionId });
    return { run: latestRun, decision };
  }

  scheduleRebaseTask(input: { issueId: string; reason?: string } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    return this.updateActiveRun(input.issueId, input, run => run.scheduleRuntimeTask({
      taskId: 'rebase-branch',
      title: 'Rebase branch',
      uses: 'mohist/rebase',
      causedBy: {
        type: 'branch-changed',
        message: input.reason ?? 'Target branch moved; rebase requested',
      },
    }));
  }

  scheduleRebaseForDrift(input: { issueId: string; baseBranch: string; observedBaseSha: string; currentBaseSha: string } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision } {
    const reason = `Base branch ${input.baseBranch} advanced from ${input.observedBaseSha} to ${input.currentBaseSha}; rebase requested by drift scan`;
    return this.scheduleRebaseTask({ issueId: input.issueId, reason, sessionId: input.sessionId, startedBy: input.startedBy });
  }

  scheduleApprovalVerdictRepair(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    const run = this.repo.loadActiveAggregate(input.issueId);
    if (!run) {
      const latestRun = this.repo.loadLatestAggregate?.(input.issueId);
      if (!latestRun) throw new Error(`No WorkflowRun for issue ${input.issueId}`);
      if (latestRun.snapshot().status !== 'failed') throw new Error(`No active or failed WorkflowRun for issue ${input.issueId}`);
      return this.handleApprovalVerdictRepairOnFailedRun(latestRun, input);
    }
    return this.handleApprovalVerdictRepairOnRunningRun(run, input);
  }

  scheduleFixReviewFindings(input: { issueId: string; stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    return this.scheduleApprovalVerdictRepair(input);
  }

  private handleApprovalVerdictRepairOnRunningRun(run: WorkflowRun, input: { stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    if (run.currentStage !== input.stage) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }
    return this.doScheduleApprovalVerdictRepair(run, input);
  }

  private handleApprovalVerdictRepairOnFailedRun(run: WorkflowRun, input: { stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    if (run.currentStage !== input.stage) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }
    return this.doScheduleApprovalVerdictRepair(run, input);
  }

  private doScheduleApprovalVerdictRepair(run: WorkflowRun, input: { stage: Stage } & WorkflowCommandOptions): { run: WorkflowRun; decision: WorkflowDecision; repairTaskId: string | null; repairStatus: CheckRepairScheduleStatus } {
    const stageRun = run.stageRun(input.stage);
    const policy = stageRun.definition.checkFailurePolicies?.find(candidate =>
      stageRun.checks.some(check => check.name === candidate.checkName && check.status === 'failed'),
    ) ?? stageRun.definition.checkFailurePolicies?.find(candidate =>
      stageRun.tasks.some(task =>
        (task.id === candidate.fixTaskId || task.id.startsWith(`${candidate.fixTaskId}:`)) &&
        (task.status === 'pending' || task.status === 'running'),
      ),
    ) ?? stageRun.definition.checkFailurePolicies?.[0];
    if (!policy) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-check-stage' };
    }
    const verdictCheckName = policy.checkName;

    const verdictCheck = stageRun.checks.find(c => c.name === verdictCheckName);
    const existingPendingFix = stageRun.tasks.find(t =>
      (t.id === policy.fixTaskId || t.id.startsWith(`${policy.fixTaskId}:`)) &&
      (t.status === 'pending' || t.status === 'running')
    );
    if (existingPendingFix) {
      run.status = 'running';
      run.failure = null;
      stageRun.reopenForRepair();
      this.repo.saveAggregate(run, input.startedBy ?? null);
      this.projection.apply({ run, decision: { events: [], nextWork: run.nextWork() }, sessionId: input.sessionId });
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: existingPendingFix.id, repairStatus: 'already-running' };
    }

    const scheduledFixCount = stageRun.scheduledFixCount(verdictCheckName);
    if (scheduledFixCount >= policy.maxAttempts) {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'exhausted' };
    }

    if (verdictCheck?.status !== 'failed') {
      return { run, decision: { events: [], nextWork: run.nextWork() }, repairTaskId: null, repairStatus: 'not-available' };
    }

    const causedBy = {
      type: 'check-failure' as const,
      checkName: verdictCheckName,
      message: verdictCheck?.message ?? `${verdictCheckName} failed`,
    };
    const fixTask = stageRun.appendFixTask(policy, causedBy);
    run.status = 'running';
    run.failure = null;
    stageRun.reopenForRepair();
    const events: import('../workflow/model').WorkflowEvent[] = [
      { type: 'fix-task-scheduled', stage: input.stage, taskId: fixTask.id, causedBy },
    ];
    this.repo.saveAggregate(run, input.startedBy ?? null);
    this.projection.apply({ run, decision: { events, nextWork: run.nextWork() }, sessionId: input.sessionId });
    return { run, decision: { events, nextWork: run.nextWork() }, repairTaskId: fixTask.id, repairStatus: 'scheduled' };
  }

  resumeDecision(issueId: string, options: WorkflowCommandOptions = {}): { run: WorkflowRun; nextWork: WorkflowWork } {
    this.reconcileIssueWorkflow(issueId, options);
    const run = this.loadForDecision(issueId);
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
    const run = this.loadActive(issueId);
    const decision = decide(run);
    this.repo.saveAggregate(run, options.startedBy ?? null);
    this.projection.apply({ run, decision, sessionId: options.sessionId });
    return { run, decision };
  }

  private loadActive(issueId: string): WorkflowRun {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(issueId)
      : this.repo.loadActiveAggregate(issueId);
    if (!run) throw new Error(`No active WorkflowRun for issue ${issueId}`);
    return run;
  }

  private loadForDecision(issueId: string): WorkflowRun {
    const run = this.repo.loadRunningAggregate
      ? this.repo.loadRunningAggregate(issueId)
      : this.repo.loadActiveAggregate(issueId);
    if (run) return run;

    const latestRun = this.repo.loadLatestAggregate?.(issueId) ?? null;
    if (latestRun?.snapshot().status === 'failed') return latestRun;
    throw new Error(`No active WorkflowRun for issue ${issueId}`);
  }

  private decisionForProjection(run: WorkflowRun, events: WorkflowDecision['events']): WorkflowDecision {
    return { events, nextWork: run.nextWork() };
  }
}
