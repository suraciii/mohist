import { CheckSuiteRepo } from '../db/check-suite-repo';
import { DatabaseManager } from '../db/database';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { IssueRepo } from '../db/issue-repo';
import { Stage, IssueStatus, type CheckState, type CheckSuiteStatus } from '../types';
import { eventBus, type EventBus } from './event-bus';
import { StageStateService, type StageCheckStatus, type StageStateStatus, type StageTaskStatus } from './stage-state-service';
import type { WorkflowDecision, WorkflowEvent, WorkflowRun } from '../workflow/domain';

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
    const projectedStage = snapshot.status === 'passed' ? Stage.Done : snapshot.currentStage;
    if (issue.stage !== projectedStage) {
      this.issueRepo.updateStage(snapshot.issueId, projectedStage);
    }

    if (snapshot.status === 'passed') {
      this.issueRepo.updateStatus(snapshot.issueId, IssueStatus.Completed);
      this.issueRepo.updateBlockedReason(snapshot.issueId, null);
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
