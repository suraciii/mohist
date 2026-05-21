import { CheckSuiteRepo } from '../db/check-suite-repo';
import { DatabaseManager } from '../db/database';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { IssueRepo } from '../db/issue-repo';
import { IssueStatus, Stage, type CheckState, type CheckSuiteStatus } from '../types';
import { eventBus, type EventBus } from './event-bus';
import { StageStateService, type StageCheckStatus, type StageStateStatus, type StageTaskStatus } from './stage-state-service';
import type { WorkflowDecision, WorkflowEvent, WorkflowRun, WorkflowRunSnapshot } from '../workflow/model';
import { validateWorkflowUseEvidence } from '../workflow/uses-catalog';

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
    const terminalStage = snapshot.stageOrder[snapshot.stageOrder.length - 1];
    if (!terminalStage) {
      return { ok: false, reason: 'terminal stage is missing' };
    }
    if (snapshot.currentStage !== terminalStage) {
      return { ok: false, reason: `current stage is ${snapshot.currentStage}, expected terminal stage ${terminalStage}` };
    }
    const stageProjection = this.validateStageStructuralCompletion(snapshot);
    if (!stageProjection.ok) return stageProjection;
    return this.validateCompletedWorkEvidence(snapshot);
  }

  private validateStageStructuralCompletion(snapshot: WorkflowRunSnapshot): { ok: true } | { ok: false; reason: string } {
    for (const stageName of snapshot.stageOrder) {
      const stageDefinition = snapshot.workflowDefinitionSnapshot.compiledStageDefinitions.find(definition => definition.stage === stageName);
      const stageRun = snapshot.stageRuns.find(candidate => candidate.stage === stageName);
      if (!stageRun) return { ok: false, reason: `stage ${stageName} run is missing` };
      if (stageRun.status !== 'passed') return { ok: false, reason: `stage ${stageName} is ${stageRun.status}` };
      if (!stageDefinition) continue;
      for (const taskDefinition of stageDefinition.tasks) {
        const task = stageRun.tasks.find(candidate => candidate.id === taskDefinition.id);
        if (task?.status !== 'completed') return { ok: false, reason: `${taskDefinition.id} task is ${task?.status ?? 'missing'}` };
      }
      for (const checkDefinition of stageDefinition.checks) {
        if (this.isApprovalCheck(stageDefinition, checkDefinition.name)) continue;
        const check = stageRun.checks.find(candidate => candidate.name === checkDefinition.name);
        if (check?.status !== 'passed') return { ok: false, reason: `${checkDefinition.name} check is ${check?.status ?? 'missing'}` };
      }
      if (this.stageRequiresApproval(stageDefinition) && stageRun.approval?.status !== 'approved') {
        return { ok: false, reason: `stage ${stageName} approval is ${stageRun.approval?.status ?? 'missing'}` };
      }
    }
    return { ok: true };
  }

  private stageRequiresApproval(stageDefinition: WorkflowRunSnapshot['workflowDefinitionSnapshot']['compiledStageDefinitions'][number]): boolean {
    if (stageDefinition.requiresApproval === false) return false;
    return Boolean(stageDefinition.requiresApproval ?? stageDefinition.approvalPolicy);
  }

  private isApprovalCheck(
    stageDefinition: WorkflowRunSnapshot['workflowDefinitionSnapshot']['compiledStageDefinitions'][number],
    checkName: string,
  ): boolean {
    return stageDefinition.checkPolicies?.some(policy => policy.checkName === checkName && policy.phase === 'approval') ?? false;
  }

  private validateCompletedWorkEvidence(snapshot: WorkflowRunSnapshot): { ok: true } | { ok: false; reason: string } {
    for (const stageDefinition of snapshot.workflowDefinitionSnapshot.compiledStageDefinitions) {
      const stageRun = snapshot.stageRuns.find(candidate => candidate.stage === stageDefinition.stage);
      if (!stageRun) continue;
      for (const task of stageRun.tasks) {
        if (task.status !== 'completed') continue;
        const taskDefinition = stageDefinition.tasks.find(candidate => candidate.id === task.id);
        const uses = taskDefinition?.uses;
        const evidence = validateWorkflowUseEvidence(uses, task.output);
        if (!evidence.ok) return { ok: false, reason: `${task.id} ${evidence.field ?? 'delivery'} evidence is missing` };
      }
      for (const check of stageRun.checks) {
        if (check.status !== 'passed') continue;
        const checkDefinition = stageDefinition.checks.find(candidate => candidate.name === check.name);
        const uses = checkDefinition?.uses;
        const evidence = validateWorkflowUseEvidence(uses, check.output);
        if (!evidence.ok) return { ok: false, reason: `${check.name} ${evidence.field ?? 'delivery'} evidence is missing` };
      }
    }
    return { ok: true };
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
    const suite = this.checkSuiteRepo.findActiveByIssueId(snapshot.issueId);
    if (!suite) return;
    const checkStage = this.checkSuiteStage(snapshot);
    if (!checkStage) return;
    const checkNames = this.checkSuiteCheckNames(snapshot, checkStage.stage);

    for (const check of checkStage.checks) {
      if (!checkNames.has(check.name)) continue;
      this.checkSuiteRepo.updateChecks(suite.id, check.name, {
        status: this.toCheckSuiteCheckStatus(check.status),
        output: check.output ?? check.message ?? undefined,
        ranAt: check.runCount > 0 ? new Date().toISOString() : undefined,
      } as CheckState);
    }

    const status = this.toCheckSuiteStatus(checkStage.status);
    if (status) this.checkSuiteRepo.updateStatus(suite.id, status);
  }

  private checkSuiteStage(snapshot: WorkflowRunSnapshot): WorkflowRunSnapshot['stageRuns'][number] | undefined {
    const current = snapshot.stageRuns.find(stageRun => stageRun.stage === snapshot.currentStage);
    if (current && this.checkSuiteCheckNames(snapshot, current.stage).size > 0) return current;
    return snapshot.stageRuns.find(stageRun => this.checkSuiteCheckNames(snapshot, stageRun.stage).size > 0);
  }

  private checkSuiteCheckNames(snapshot: WorkflowRunSnapshot, stage: Stage): Set<string> {
    const stageDefinition = snapshot.workflowDefinitionSnapshot.compiledStageDefinitions.find(definition => definition.stage === stage);
    const names = new Set<string>();
    for (const check of stageDefinition?.checks ?? []) {
      names.add(check.name);
    }
    if (stageDefinition?.approvalPolicy) {
      names.add(stageDefinition.approvalPolicy.checkName);
    }
    return names;
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
