import { IssueStatus, Stage } from '../types';
import type { IssueService, ProjectService } from './index';
import type { AgentRunnerService } from './agent-runner-service';
import type { IssueQueueStatus } from './agent-runner-service';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { WorktreeManager } from '../git/worktree-manager';
import type { IssuePrerequisiteService } from './issue-prerequisite-service';
import { classifyMergeDelivery, isCurrentStageApproval } from '../workflow/issue-lifecycle';
import { findChangeDir } from '../openspec/detector';

type Issue = NonNullable<ReturnType<IssueService['getByNumber']>>;

type WorkflowActionStatus = 200 | 202 | 400 | 404 | 409 | 500;

export type IssueWorkflowResult<T = unknown> =
  | { ok: true; status?: WorkflowActionStatus; data: T }
  | { ok: false; status: WorkflowActionStatus; error: string; data?: unknown };

export type IssueWorkflowServiceDeps = {
  issueService: IssueService;
  projectService: ProjectService;
  agentRunner?: AgentRunnerService;
  workflowLogRepo?: WorkflowLogRepo;
  worktreeManager?: WorktreeManager | null;
  issuePrerequisiteService?: IssuePrerequisiteService;
  getIssueRepo: () => {
    findPendingApprovalByIssueId(issueId: string): unknown;
    updateBlockedReason(issueId: string, reason: string | null): void;
    updateRetryCount(issueId: string, count: number): void;
    updateStatus(issueId: string, status: IssueStatus): void;
    clearApprovalState(issueId: string): void;
  };
};

export class IssueWorkflowService {
  constructor(private readonly deps: IssueWorkflowServiceDeps) {}

  start(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const { projectId, issue } = found;
    if (this.deps.issuePrerequisiteService) {
      const startEligibility = this.deps.issuePrerequisiteService.assertStartEligible(projectId, issue);
      if (!startEligibility.startable) {
        return this.fail(
          startEligibility.message ?? `Issue #${number} is not startable: ${startEligibility.reason}`,
          400,
          { startEligibility },
        );
      }
    } else {
      const rejection = this.getLifecycleStartRejection(issue);
      if (rejection) return this.fail(rejection, 400);
    }

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    const queued = agentRunner.enqueue(issue.id, 'start-pipeline');
    return this.done({
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} enqueued for start-pipeline`,
    }, 202);
  }

  stop(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    agentRunner.cancelAll(found.issue.id);
    this.deps.issueService.setStatus(found.issue.id, IssueStatus.Interrupted);
    return this.done({ ok: true as const, issueNumber: number });
  }

  resume(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;

    if (!this.canResumeIssue(issue)) {
      return this.fail(`Issue #${number} cannot be resumed (current status: ${issue.status}). Use retry or rerun instead.`, 409);
    }

    const agentRunner = this.deps.agentRunner;
    agentRunner?.recoverSingleIssueById(issue.id);

    const resumedIssue = this.deps.issueService.resume(projectId, number);
    if (!resumedIssue) return this.fail(`Failed to resume issue #${number}`, 500);

    if (!agentRunner) {
      return this.done({
        issue: this.deps.issueService.getByNumber(projectId, number) ?? resumedIssue,
        message: `Issue #${number} resumed at stage ${issue.stage}.`,
      });
    }

    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      issue: this.deps.issueService.getByNumber(projectId, number) ?? resumedIssue,
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} resumed and enqueued for resume-pipeline`,
    }, 202);
  }

  approve(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    const issueRepo = this.deps.getIssueRepo();
    if (!isCurrentStageApproval(issue, issue.stage, 'awaiting')) {
      const pendingIssue = issueRepo.findPendingApprovalByIssueId(issue.id);
      if (!pendingIssue) {
        return this.fail(`No pending approval for issue #${number}. The workflow may have completed or not been started. Try: mo issue start ${number}`, 400);
      }
    }

    if (issue.approvalState) {
      issueRepo.clearApprovalState(issue.id);
    }

    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      issue: this.deps.issueService.getByNumber(projectId, number),
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} approved, enqueued for resume-pipeline`,
    }, 202);
  }

  reject(number: number, message?: string): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;

    if (!isCurrentStageApproval(issue, issue.stage, 'awaiting')) {
      return this.fail(`Issue #${number} is not awaiting approval at current stage`, 400);
    }

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
    if (queueStatus.running) return this.fail(`Issue #${number} has a running task. Wait for it to complete first.`, 400);

    if (message) this.deps.issueService.createComment(issue.id, message);

    const issueRepo = this.deps.getIssueRepo();
    issueRepo.clearApprovalState(issue.id);
    issueRepo.updateBlockedReason(issue.id, null);
    issueRepo.updateRetryCount(issue.id, 0);
    issueRepo.updateStatus(issue.id, IssueStatus.Active);

    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      issue: this.deps.issueService.getByNumber(projectId, number),
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} rejected, workflow resumed for rework`,
    }, 202);
  }

  message(number: number, message: unknown): IssueWorkflowResult {
    if (!message || typeof message !== 'string') {
      return this.fail('message is required and must be a string', 400);
    }

    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);
    if (!agentRunner.isIssueAwaitingApproval(issue.id)) return this.fail(`Workflow is not paused for issue #${number}`, 409);

    this.deps.issueService.createComment(issue.id, message);
    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      issue: this.deps.issueService.getByNumber(projectId, number),
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Message sent to issue #${number}, workflow resumed`,
    }, 202);
  }

  retry(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;

    if (issue.stage === Stage.Backlog) return this.fail(`Issue #${number} is in ${issue.stage} stage. Use start instead of retry.`, 400);
    if (issue.stage === Stage.Done) return this.fail(`Issue #${number} is in done stage. Retry is unavailable after delivery; manual intervention is required.`, 409);

    const deliveryStatus = classifyMergeDelivery(issue);
    if (deliveryStatus === 'merged' || deliveryStatus === 'integrating') {
      return this.fail(`Issue #${number} has already been merged or is in integrate stage. Automatic retry is disabled; manual intervention is required.`, 409);
    }

    const project = this.deps.projectService.getById(projectId);
    if (!project) return this.fail('Project not found', 404);
    if (this.deps.worktreeManager?.exists && !this.deps.worktreeManager.exists(project.name, number)) {
      return this.fail(`Workspace for issue #${number} has been removed. Retry requires the workspace to be available.`, 409);
    }
    if (this.deps.worktreeManager) {
      const worktreePath = this.deps.worktreeManager.getPath(project.name, number);
      if (worktreePath && !findChangeDir(worktreePath, number)) {
        return this.fail(`No change directory found for issue #${number}. The workspace may be incomplete.`, 409);
      }
    }

    const issueRepo = this.deps.getIssueRepo();
    issueRepo.updateBlockedReason(issue.id, null);
    issueRepo.updateRetryCount(issue.id, 0);
    issueRepo.updateStatus(issue.id, IssueStatus.Active);

    const message = `Issue #${number} retrying from ${issue.stage} stage`;
    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.done({ message });
    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({ taskId: queued.taskId, status: queued.status, queuePosition: queued.queuePosition, message }, 202);
  }

  rerun(number: number, stage?: Stage): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { projectId, issue } = found;
    const targetStage = stage ?? issue.stage;

    if (issue.stage === Stage.Backlog) return this.fail(`Issue #${number} is in ${issue.stage} stage. Use start instead of rerun.`, 400);
    if (issue.stage === Stage.Done) return this.fail(`Issue #${number} is in done stage. Rerun is not supported for completed issues.`, 400);
    if (issue.stage !== targetStage) return this.fail(`Issue #${number} is not in ${targetStage} stage (current: ${issue.stage})`, 409);

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    agentRunner.cancelAll(issue.id);
    const issueRepo = this.deps.getIssueRepo();
    issueRepo.clearApprovalState(issue.id);
    issueRepo.updateBlockedReason(issue.id, null);
    issueRepo.updateRetryCount(issue.id, 0);
    issueRepo.updateStatus(issue.id, IssueStatus.Active);

    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      issue: this.deps.issueService.getByNumber(projectId, number),
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} rerun from ${targetStage} stage`,
    }, 202);
  }

  rebase(number: number): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    const { issue } = found;
    if (issue.stage === Stage.Backlog) return this.fail(`Issue #${number} has not started. Start the issue before rebasing.`, 400);
    if (issue.stage === Stage.Done) return this.fail(`Issue #${number} is done; rebase must happen before completion.`, 409);

    const agentRunner = this.deps.agentRunner;
    if (!agentRunner) return this.fail('AgentRunnerService not configured', 500);

    const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
    return this.done({
      taskId: queued.taskId,
      status: queued.status,
      queuePosition: queued.queuePosition,
      message: `Issue #${number} enqueued for workflow rebase/resume`,
    }, 202);
  }

  logs(number: number, eventType?: string): IssueWorkflowResult {
    const found = this.requireIssue(number);
    if (!found.ok) return found;
    if (!this.deps.workflowLogRepo) return this.fail('WorkflowLog not configured', 500);

    const entries = this.deps.workflowLogRepo.findByIssueId(found.issue.id, eventType).map(log => ({
      id: log.id,
      eventType: log.eventType,
      data: (() => { try { return JSON.parse(log.data); } catch { return log.data; } })(),
      createdAt: log.createdAt,
    }));
    return this.done(entries);
  }

  private requireIssue(number: number):
    | { ok: true; projectId: string; issue: Issue }
    | { ok: false; status: WorkflowActionStatus; error: string } {
    const projectId = this.deps.projectService.getCurrentId();
    if (!projectId) return { ok: false, status: 400, error: 'No active project. Use: mo project use <name>' };

    const issue = this.deps.issueService.getByNumber(projectId, number);
    if (!issue) return { ok: false, status: 404, error: `Issue #${number} not found` };
    return { ok: true, projectId, issue };
  }

  private canResumeIssue(issue: Issue): boolean {
    return issue.status === IssueStatus.Paused
      || issue.status === IssueStatus.Interrupted
      || issue.status === IssueStatus.Blocked;
  }

  private getLifecycleStartRejection(issue: Issue): string | null {
    if (issue.status === IssueStatus.Blocked) return `Issue #${issue.number} is blocked. Use: mo issue retry ${issue.number} or mo issue rerun ${issue.number}`;
    if (issue.status === IssueStatus.Closed) return `Issue #${issue.number} is closed. Run: mo issue reopen ${issue.number}`;
    if (issue.status === IssueStatus.Paused) return `Issue #${issue.number} is paused. Run: mo issue approve ${issue.number} to resume`;
    if (issue.stage !== Stage.Backlog) return `Issue #${issue.number} is not in a startable stage (current: ${issue.stage}). Only backlog issues can be started.`;
    return null;
  }

  private done<T>(data: T, status?: WorkflowActionStatus): IssueWorkflowResult<T> {
    return { ok: true, status, data };
  }

  private fail(error: string, status: WorkflowActionStatus, data?: unknown): IssueWorkflowResult<never> {
    return data === undefined ? { ok: false, status, error } : { ok: false, status, error, data };
  }
}
