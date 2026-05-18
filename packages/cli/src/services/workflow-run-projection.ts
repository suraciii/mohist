import { CheckSuiteRepo } from '../db/check-suite-repo';
import { DatabaseManager } from '../db/database';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { IssueRepo } from '../db/issue-repo';
import { Stage, IssueStatus, type CheckState, type CheckSuiteStatus } from '../types';
import { eventBus, type EventBus } from './event-bus';
import { StageStateService, type StageCheckStatus, type StageStateStatus, type StageTaskStatus } from './stage-state-service';
import type { StageRunSnapshot, WorkflowDecision, WorkflowEvent, WorkflowRun, WorkflowRunSnapshot } from '../workflow/domain';

interface IssueProjectionRow {
  id: string;
  project_id: string;
  stage: string;
}

export interface WorkflowRunProjectionInput {
  run: WorkflowRun;
  decision: WorkflowDecision;
  sessionId?: string | null;
}

export class WorkflowRunProjection {
  private issueRepo: IssueRepo;
  private stageStateService: StageStateService;
  private checkSuiteRepo: CheckSuiteRepo;
  private workflowLogRepo: WorkflowLogRepo;

  constructor(
    private db: DatabaseManager,
    private bus: EventBus = eventBus,
  ) {
    this.issueRepo = new IssueRepo(db);
    this.stageStateService = new StageStateService(db);
    this.checkSuiteRepo = new CheckSuiteRepo(db);
    this.workflowLogRepo = new WorkflowLogRepo(db);
  }

  apply(input: WorkflowRunProjectionInput): void {
    const snapshot = input.run.snapshot();
    const issue = this.db.get<IssueProjectionRow>('SELECT id, project_id, stage FROM issues WHERE id = ?', [snapshot.issueId]);
    if (!issue) return;

    this.db.transaction(() => {
      this.projectIssue(issue, input.run);
      this.projectStageStates(input.run);
      this.projectCheckSuite(input.run);
      this.projectWorkflowLog(snapshot.issueId, input.sessionId ?? null, input.decision.events);
    });

    this.emitSse(issue, input.run, input.decision.events);
  }

  private projectIssue(issue: IssueProjectionRow, run: WorkflowRun): void {
    const snapshot = run.snapshot();
    const completionProjection = this.validateCompletionProjection(snapshot);
    const projectedStage = snapshot.status === 'passed' && completionProjection.ok ? Stage.Done : snapshot.currentStage;
    if (issue.stage !== projectedStage) {
      this.issueRepo.updateStage(snapshot.issueId, projectedStage);
    }

    if (snapshot.status === 'passed') {
      if (!completionProjection.ok) {
        this.issueRepo.updateStatus(snapshot.issueId, IssueStatus.Blocked);
        this.issueRepo.updateBlockedReason(snapshot.issueId, `WorkflowRun projection rejected impossible passed snapshot: ${completionProjection.reason}`);
        this.issueRepo.clearApprovalState(snapshot.issueId);
        return;
      }
      this.issueRepo.updateStatus(snapshot.issueId, IssueStatus.Completed);
      this.issueRepo.updateBlockedReason(snapshot.issueId, null);
      this.issueRepo.clearApprovalState(snapshot.issueId);
      return;
    }

    const recoverySummary = run.workflowRecoverySummary();
    const interruptedAttempt = this.findInterruptedAttempt(snapshot);
    if (recoverySummary === 'waiting-for-recovery' || interruptedAttempt) {
      this.issueRepo.updateStatus(snapshot.issueId, interruptedAttempt ? IssueStatus.Interrupted : IssueStatus.Blocked);
      this.issueRepo.updateBlockedReason(snapshot.issueId, interruptedAttempt?.diagnostic ?? interruptedAttempt?.error ?? 'Workflow is waiting for recovery');
      this.issueRepo.clearApprovalState(snapshot.issueId);
      return;
    }

    if (snapshot.status === 'failed') {
      this.issueRepo.updateStatus(snapshot.issueId, IssueStatus.Blocked);
      this.issueRepo.clearApprovalState(snapshot.issueId);
      return;
    }

    this.issueRepo.updateStatus(snapshot.issueId, IssueStatus.Active);
    this.issueRepo.updateBlockedReason(snapshot.issueId, null);

    const awaitingApproval = snapshot.stageRuns.find(stage => stage.approval?.status === 'awaiting');
    if (awaitingApproval?.approval) {
      this.issueRepo.setApprovalState(snapshot.issueId, {
        stage: awaitingApproval.stage,
        status: 'awaiting',
        output: awaitingApproval.approval.output,
        requestedAt: awaitingApproval.approval.requestedAt,
        respondedAt: awaitingApproval.approval.respondedAt ?? undefined,
      });
    } else {
      this.issueRepo.clearApprovalState(snapshot.issueId);
    }
  }

  private findInterruptedAttempt(snapshot: WorkflowRunSnapshot) {
    const currentStage = snapshot.stageRuns.find(stage => stage.stage === snapshot.currentStage);
    if (!currentStage) return null;
    for (const task of currentStage.tasks) {
      if (task.latestAttempt?.state === 'interrupted') return task.latestAttempt;
    }
    for (const check of currentStage.checks) {
      if (check.latestAttempt?.state === 'interrupted') return check.latestAttempt;
    }
    return null;
  }

  private validateCompletionProjection(snapshot: WorkflowRunSnapshot): { ok: true } | { ok: false; reason: string } {
    if (snapshot.status !== 'passed') return { ok: true };
    if (snapshot.stageOrder[snapshot.stageOrder.length - 1] !== Stage.Integrate) {
      return { ok: false, reason: 'final stage is not integrate' };
    }
    if (snapshot.currentStage !== Stage.Integrate) {
      return { ok: false, reason: `current stage is ${snapshot.currentStage}, expected integrate` };
    }

    const integrate = snapshot.stageRuns.find(stageRun => stageRun.stage === Stage.Integrate);
    if (!integrate) return { ok: false, reason: 'integrate stage run is missing' };
    if (integrate.status !== 'passed') return { ok: false, reason: `integrate stage is ${integrate.status}` };

    return this.validateIntegrateDeliveryEvidence(integrate);
  }

  private validateIntegrateDeliveryEvidence(stageRun: StageRunSnapshot): { ok: true } | { ok: false; reason: string } {
    const specSync = stageRun.tasks.find(task => task.id === 'integrate:spec-sync');
    if (specSync?.status !== 'completed') return { ok: false, reason: 'integrate:spec-sync evidence is missing' };

    const archive = stageRun.tasks.find(task => task.id === 'integrate:archive-change');
    if (archive?.status !== 'completed' || !archive.output || typeof archive.output !== 'object') {
      return { ok: false, reason: 'integrate:archive-change evidence is missing' };
    }
    const archiveOutput = this.unwrapTaskOutput(archive.output);
    if (!archiveOutput) {
      return { ok: false, reason: 'integrate:archive-change evidence is missing' };
    }
    const hasArchivePath = typeof archiveOutput.archivePath === 'string' && archiveOutput.archivePath.length > 0;
    const hasArchiveSuccess = archiveOutput.success === true;
    if (!hasArchivePath && !hasArchiveSuccess) {
      return { ok: false, reason: 'integrate:archive-change archivePath is missing' };
    }

    const merge = stageRun.tasks.find(task => task.id === 'integrate:merge');
    if (merge?.status !== 'completed') return { ok: false, reason: 'integrate:merge evidence is missing' };
    const delivery = stageRun.freezePoint?.delivery ?? {};
    if (!delivery.landedSha) {
      return { ok: false, reason: 'integrate merge landedSha evidence is missing' };
    }

    const health = stageRun.checks.find(check => check.name === 'health:integrate');
    if (health?.status !== 'passed') return { ok: false, reason: 'health:integrate evidence is missing' };

    return { ok: true };
  }

  private unwrapTaskOutput(output: unknown): Record<string, unknown> | null {
    if (!output || typeof output !== 'object') return null;
    const data = output as Record<string, unknown>;
    if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
      return data.result as Record<string, unknown>;
    }
    return data;
  }

  private projectStageStates(run: WorkflowRun): void {
    const snapshot = run.snapshot();
    for (const stageRun of snapshot.stageRuns) {
      this.stageStateService.ensureStage(snapshot.issueId, stageRun.stage);
      this.stageStateService.setStageStatus(snapshot.issueId, stageRun.stage, stageRun.status as StageStateStatus);

      for (const task of stageRun.tasks) {
        this.stageStateService.upsertTask(snapshot.issueId, stageRun.stage, {
          taskId: task.id,
          title: task.title,
          status: task.status as StageTaskStatus,
          source: 'static',
          order: task.order,
          attempts: task.attempts,
          duration: task.duration,
          artifacts: task.artifacts,
          output: task.output ?? undefined,
        });
      }

      for (const check of stageRun.checks) {
        this.stageStateService.upsertCheck(snapshot.issueId, stageRun.stage, {
          checkName: check.name,
          status: check.status as StageCheckStatus,
          message: check.message,
          output: check.output,
          runCount: check.runCount,
        });
      }

      if (stageRun.approval) {
        this.stageStateService.setApproval(snapshot.issueId, stageRun.stage, {
          status: stageRun.approval.status,
          output: stageRun.approval.output,
          requestedAt: stageRun.approval.requestedAt,
          respondedAt: stageRun.approval.respondedAt,
        });
      } else {
        this.stageStateService.clearApproval(snapshot.issueId, stageRun.stage);
      }
    }
  }

  private projectCheckSuite(run: WorkflowRun): void {
    const snapshot = run.snapshot();
    const checkStage = snapshot.stageRuns.find(stage => stage.stage === Stage.Check);
    const suite = this.checkSuiteRepo.findActiveByIssueId(snapshot.issueId);
    if (!checkStage || !suite) return;

    for (const check of checkStage.checks) {
      if (check.name !== 'health:check' && check.name !== 'review-passed' && check.name !== 'merge-ready' && check.name !== 'user-approval') continue;
      this.checkSuiteRepo.updateChecks(suite.id, check.name, {
        status: this.toCheckSuiteCheckStatus(check.status),
        output: check.output ?? check.message ?? undefined,
        ranAt: check.runCount > 0 ? new Date().toISOString() : undefined,
      } as CheckState);
    }

    const status = this.toCheckSuiteStatus(checkStage.status);
    if (status) this.checkSuiteRepo.updateStatus(suite.id, status);
  }

  private projectWorkflowLog(issueId: string, sessionId: string | null, events: WorkflowEvent[]): void {
    for (const event of events) {
      this.workflowLogRepo.insert(issueId, sessionId, `workflow_run.${event.type}`, event);
    }
  }

  private emitSse(issue: IssueProjectionRow, run: WorkflowRun, events: WorkflowEvent[]): void {
    const snapshot = run.snapshot();
    for (const event of events) {
      if (event.type === 'stage-started') {
        this.bus.emit('stage_changed', { issueId: snapshot.issueId, projectId: issue.project_id, from: issue.stage, to: event.stage });
      } else if (event.type === 'stage-retried') {
        this.bus.emit('stage_changed', { issueId: snapshot.issueId, projectId: issue.project_id, from: issue.stage, to: event.stage });
      } else if (event.type === 'approval-requested') {
        this.bus.emit('approval_requested', { issueId: snapshot.issueId, projectId: issue.project_id, stage: event.stage });
      } else if (event.type === 'task-completed' || event.type === 'task-failed') {
        const stageRun = snapshot.stageRuns.find(stage => stage.stage === event.stage);
        const task = stageRun?.tasks.find(candidate => candidate.id === event.taskId);
        if (task) {
          this.bus.emit('stage_task_update', {
            issueId: snapshot.issueId,
            projectId: issue.project_id,
            stage: event.stage,
            taskId: task.id,
            taskTitle: task.title,
            status: task.status === 'failed' ? 'failed' : 'completed',
            attempt: task.attempts,
            artifacts: task.artifacts,
          });
        }
      } else if (event.type === 'check-recorded') {
        this.bus.emit('check_update', {
          issueId: snapshot.issueId,
          projectId: issue.project_id,
          checkName: event.checkName,
          status: event.status,
        });
      }
    }
  }

  private toCheckSuiteCheckStatus(status: string): CheckState['status'] {
    if (status === 'passed') return 'passed';
    if (status === 'failed' || status === 'error') return 'failed';
    if (status === 'running') return 'running';
    return 'pending';
  }

  private toCheckSuiteStatus(status: string): CheckSuiteStatus | null {
    if (status === 'awaiting-approval') return 'awaiting-approval';
    if (status === 'passed') return 'passed';
    if (status === 'failed') return 'failed';
    if (status === 'running') return 'running';
    return null;
  }
}
