import { Hono } from 'hono';
import * as fs from 'fs';
import * as path from 'path';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment, Priority, MergeState, normalizePriority } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import type { ConflictResolutionDeps } from '../services/conflict-resolution';
import { WorktreeManager } from '../git/worktree-manager';
import { MergeQueue } from '../git/merge-queue';
import type { LlmConfig } from '../agent-runtime';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { SessionStreamLogRepo, type SessionStreamLogEntry } from '../db/session-stream-log-repo';
import { CoderSessionRepo } from '../db/coder-session-repo';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { CheckSuiteRepo } from '../db/check-suite-repo';
import { StageExecutionRepo } from '../db/stage-execution-repo';
import { detectOpenSpecChange, findChangeDir } from '../openspec/detector';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Log } from '../util/log';
import type { IssueQueueStatus } from '../services/agent-runner-service';
import { classifyMergeDelivery, isCurrentStageApproval } from '../workflow/issue-lifecycle';
import { assembleSessionTranscript } from '../services/session-transcript-service';
import type { PostMergeFinalizer } from '../services/post-merge-finalizer';
import { isValidModelId } from '../config/model-resolution';
import { getLatestCheckResult, type CheckResult } from '../workflow/stage-context';
import type { StageStateService } from '../services/stage-state-service';
import type { WorkflowRunService } from '../services/workflow-run-service';
import type { WorkflowRunWithStageRuns, WorkflowStageRunWithTasksAndChecks } from '../db/workflow-run-repo';
import { WorkflowApplicationService } from '../services/workflow-application-service';

type ChangesUnavailableReason = 'worktree_removed' | 'branch_missing' | 'not_started' | 'git_error';

type ChangesAvailability =
  | { available: true; reason: null }
  | { available: false; reason: ChangesUnavailableReason; message: string };

type ChangesSummary = {
  filesChanged: number;
  commits: number;
  additions: number;
  deletions: number;
};

type DiffFile = {
  file: string;
  additions: number;
  deletions: number;
  diff: string;
  isBinary: boolean;
};

type CommitEntry = {
  hash: string;
  shortHash: string;
  message: string;
  author: string;
  date: string;
  filesChanged: number;
  additions: number;
  deletions: number;
  files: string[];
};

type ComparisonMetadata = {
  base: string;
  head: string;
  mergeBase: string;
  ahead: number;
  behind: number;
  canFastForward: boolean;
  comparison: 'merge-base';
};

type IssueDiffResponse = ChangesAvailability & ComparisonMetadata & {
  summary: ChangesSummary;
  files: DiffFile[];
};

type IssueCommitsResponse = ChangesAvailability & ComparisonMetadata & {
  summary: ChangesSummary & { commits: number };
  commits: CommitEntry[];
};

type CommitDiffResponse = ChangesAvailability & {
  hash: string;
  diff: string;
};

function unavailableChangesData(issue: Issue, message: string) {
  const reason = issue.stage === Stage.Backlog
    ? 'not_started' as const
    : 'worktree_removed' as const;

  return {
    available: false as const,
    reason,
    message,
  };
}

const log = Log.create({ service: 'issue' });

const execFileAsync = promisify(execFile);

function getLatestCheckStageReviewPassed(issueId: string, stageExecutionRepo?: StageExecutionRepo): CheckResult | undefined {
  if (!stageExecutionRepo) return undefined;

  const latestCheckExecution = stageExecutionRepo
    .findByIssueId(issueId)
    .filter(execution => execution.stage === Stage.Check)
    .at(-1);

  if (!latestCheckExecution) return undefined;
  return getLatestCheckResult(latestCheckExecution.checkResults as CheckResult[], 'review-passed') ??
    getLatestCheckResult(latestCheckExecution.checkResults as CheckResult[], 'ai-review');
}

function getWorkflowRunCheckReviewPassed(issueId: string, workflowRunService?: WorkflowRunService): CheckResult | undefined {
  const checkStage = workflowRunService
    ?.getActiveRunForIssue(issueId)
    ?.stageRuns
    .find(stageRun => stageRun.stage === Stage.Check);
  if (!checkStage) return undefined;

  const reviewPassed = checkStage.checks.find(check => check.checkName === 'review-passed');
  const aiReview = checkStage.checks.find(check => check.checkName === 'ai-review');
  const check = reviewPassed ?? aiReview;
  if (!check) return undefined;

  return {
    name: check.checkName,
    status: check.status === 'passed'
      ? 'pass'
      : check.status === 'pending' || check.status === 'running'
        ? 'pending'
        : check.status === 'error'
          ? 'error'
          : 'fail',
    message: check.message ?? undefined,
    output: check.output ?? undefined,
  };
}

function getAuthoritativeCheckReviewPassed(
  issueId: string,
  workflowRunService?: WorkflowRunService,
  stageExecutionRepo?: StageExecutionRepo,
): CheckResult | undefined {
  return getWorkflowRunCheckReviewPassed(issueId, workflowRunService) ??
    getLatestCheckStageReviewPassed(issueId, stageExecutionRepo);
}

type IssueComparisonContext = {
  available: true;
  base: string;
  head: string;
  mergeBase: string;
  ahead: number;
  behind: number;
  canFastForward: boolean;
  comparison: 'merge-base';
} | {
  available: false;
  reason: ChangesUnavailableReason;
  message: string;
};

async function resolveIssueComparisonContext(
  projectId: string,
  issue: Issue,
  projectService: ProjectService,
  worktreeManager: WorktreeManager | null,
): Promise<IssueComparisonContext> {
  const project = projectService.getById(projectId);
  if (!project) {
    return { available: false, reason: 'branch_missing', message: 'Project not found' };
  }

  const branchName = `mo/issue-${issue.number}`;

  if (!worktreeManager || !worktreeManager.exists(project.name, issue.number)) {
    if (issue.stage === Stage.Backlog) {
      return { available: false, reason: 'not_started', message: 'Issue has not started yet. Start the issue to see changes.' };
    }
    return { available: false, reason: 'worktree_removed', message: 'Workspace has been removed. Diff is only available while the issue worktree is retained.' };
  }

  let branchExists = false;
  try {
    const revOutput = await execFileAsync('git', ['rev-parse', '--verify', `refs/heads/${branchName}`], { cwd: project.path });
    branchExists = revOutput.stdout.trim().length > 0;
  } catch {
    branchExists = false;
  }

  if (!branchExists) {
    return { available: false, reason: 'branch_missing', message: `Branch ${branchName} not found. The issue branch may have been deleted.` };
  }

  let baseExists = false;
  try {
    const revOutput = await execFileAsync('git', ['rev-parse', '--verify', `refs/heads/${project.baseBranch}`], { cwd: project.path });
    baseExists = revOutput.stdout.trim().length > 0;
  } catch {
    baseExists = false;
  }

  if (!baseExists) {
    return { available: false, reason: 'branch_missing', message: `Base branch ${project.baseBranch} not found.` };
  }

  let mergeBaseOutput: { stdout: string };
  try {
    mergeBaseOutput = await execFileAsync('git', ['merge-base', project.baseBranch, branchName], { cwd: project.path });
  } catch {
    return { available: false, reason: 'git_error', message: 'Failed to resolve merge base. Check that the branch has commits.' };
  }

  const mergeBase = mergeBaseOutput.stdout.trim();
  if (!mergeBase) {
    return { available: false, reason: 'git_error', message: 'Failed to resolve merge base.' };
  }

  const status = await worktreeManager.getWorktreeStatus(project.path, project.name, issue.number, project.baseBranch);

  return {
    available: true,
    base: project.baseBranch,
    head: branchName,
    mergeBase,
    ahead: status.ahead,
    behind: status.behind,
    canFastForward: status.canFastForward,
    comparison: 'merge-base',
  };
}

function taskCause(task: { causedByType: string | null; causedByCheckName: string | null; causedByTaskId: string | null; reason: string | null }) {
  if (!task.causedByType) return null;
  return {
    type: task.causedByType,
    checkName: task.causedByCheckName ?? undefined,
    taskId: task.causedByTaskId ?? undefined,
    message: task.reason ?? undefined,
  };
}

function deliveryMetadata(stageRun: WorkflowStageRunWithTasksAndChecks) {
  if (stageRun.stage !== Stage.Integrate) return null;
  const specSync = stageRun.tasks.find(task => task.taskId === 'integrate:spec-sync');
  const archive = stageRun.tasks.find(task => task.taskId === 'integrate:archive-change');
  const merge = stageRun.tasks.find(task => task.taskId === 'integrate:merge');
  const health = stageRun.checks.find(check => check.checkName === 'health:integrate');
  const mergeOutput = merge?.output && typeof merge.output === 'object' ? merge.output as Record<string, unknown> : {};

  if (!specSync && !archive && !merge && !health) return null;
  return {
    specSync: specSync ? { status: specSync.status, output: specSync.output } : null,
    archive: archive ? { status: archive.status, output: archive.output } : null,
    merge: merge ? {
      status: merge.status,
      output: merge.output,
      targetBranch: typeof mergeOutput.targetBranch === 'string' ? mergeOutput.targetBranch : null,
      baseSha: typeof mergeOutput.baseSha === 'string' ? mergeOutput.baseSha : null,
      candidateHeadSha: typeof mergeOutput.candidateHeadSha === 'string' ? mergeOutput.candidateHeadSha : null,
      landedSha: typeof mergeOutput.landedSha === 'string' ? mergeOutput.landedSha : null,
      rebased: typeof mergeOutput.rebased === 'boolean' ? mergeOutput.rebased : null,
    } : null,
    health: health ? { status: health.status, message: health.message, output: health.output } : null,
    frozen: merge?.status === 'completed',
  };
}

function failureDetails(stageRun: WorkflowStageRunWithTasksAndChecks) {
  if (stageRun.status !== 'failed') return null;
  const failedTask = stageRun.tasks.find(task => task.status === 'failed');
  if (failedTask) {
    return {
      reason: 'task-failed',
      stage: stageRun.stage,
      taskId: failedTask.taskId,
      message: failedTask.reason,
      causedBy: taskCause(failedTask),
    };
  }

  const failedCheck = stageRun.checks.find(check => check.status === 'failed' || check.status === 'error');
  if (failedCheck) {
    const merged = stageRun.stage === Stage.Integrate && stageRun.tasks.some(task => task.taskId === 'integrate:merge' && task.status === 'completed');
    return {
      reason: merged && failedCheck.checkName === 'health:integrate' ? 'post-merge-health-failed' : 'check-unrepaired',
      stage: stageRun.stage,
      checkName: failedCheck.checkName,
      message: failedCheck.message,
    };
  }

  if (stageRun.approvalStatus === 'rejected') {
    return { reason: 'approval-rejected', stage: stageRun.stage, message: null };
  }
  return null;
}

function projectWorkflowRun(run: WorkflowRunWithStageRuns) {
  const stageRuns = run.stageRuns.map(stageRun => {
    const failure = failureDetails(stageRun);
    const delivery = deliveryMetadata(stageRun);
    return {
      id: stageRun.id,
      workflowRunId: stageRun.workflowRunId,
      stage: stageRun.stage,
      status: stageRun.status,
      stageOrder: stageRun.stageOrder,
      tasks: stageRun.tasks.map(task => ({
        id: task.id,
        taskId: task.taskId,
        title: task.title,
        status: task.status,
        taskOrder: task.taskOrder,
        attempts: task.attempts,
        duration: task.duration,
        artifacts: task.artifacts,
        output: task.output,
        reason: task.reason,
        causedBy: taskCause(task),
        startedAt: task.startedAt,
        completedAt: task.completedAt,
        updatedAt: task.updatedAt,
      })),
      checks: stageRun.checks.map(check => ({
        id: check.id,
        checkName: check.checkName,
        title: check.title,
        status: check.status,
        message: check.message,
        output: check.output,
        runCount: check.runCount,
        lastRunAt: check.lastRunAt,
        updatedAt: check.updatedAt,
      })),
      approval: stageRun.approvalStatus ? {
        status: stageRun.approvalStatus,
        output: stageRun.approvalOutput,
        requestedAt: stageRun.approvalRequestedAt,
        respondedAt: stageRun.approvalRespondedAt,
      } : null,
      approvalStatus: stageRun.approvalStatus,
      approvalOutput: stageRun.approvalOutput,
      approvalRequestedAt: stageRun.approvalRequestedAt,
      approvalRespondedAt: stageRun.approvalRespondedAt,
      failure,
      deliveryMetadata: delivery,
      attempts: 0,
      startedAt: stageRun.startedAt,
      completedAt: stageRun.completedAt,
      updatedAt: stageRun.updatedAt,
    };
  });

  return {
    issueId: run.issueId,
    issueNumber: run.issueNumber,
    id: run.id,
    status: run.status,
    currentStage: run.currentStage,
    stageRuns,
    failure: stageRuns.find(stageRun => stageRun.failure)?.failure ?? null,
  };
}

export function createIssueRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  _llmConfig?: LlmConfig,
  agentRunner?: AgentRunnerService,
  workflowLogRepo?: WorkflowLogRepo,
  sessionStreamLogRepo?: SessionStreamLogRepo,
  coderSessionRepo?: CoderSessionRepo,
  _opencodeBinPath?: string,
  mergeQueue?: MergeQueue,
  checkpointRepo?: PipelineCheckpointRepo,
  _resolveConflictsDeps?: ConflictResolutionDeps,
  checkSuiteRepo?: CheckSuiteRepo,
  stageExecutionRepo?: StageExecutionRepo,
  _postMergeFinalizer?: PostMergeFinalizer,
  stageStateService?: StageStateService,
  workflowRunService?: WorkflowRunService,
): Hono {
  const app = new Hono();

  const clearApprovalEverywhere = (issueId: string, stage?: Stage) => {
    const issueRepo = stateManager.getIssueRepo();
    issueRepo.clearApprovalState(issueId);
    if (stageStateService && stage) {
      stageStateService.clearApproval(issueId, stage);
    }
  };

  const createWorkflowApplicationService = (): WorkflowApplicationService | null => {
    if (!workflowRunService) return null;
    return new WorkflowApplicationService(workflowRunService.getDatabaseManager());
  };

  const activeWorkflowRunExists = (issueId: string): boolean => Boolean(workflowRunService?.getActiveRunForIssue(issueId));

  const getIssueTasksPath = (projectId: string, issue: Issue): string | undefined => {
    const project = projectService.getById(projectId);
    const worktreePath = project && worktreeManager ? worktreeManager.getPath(project.name, issue.number) : null;
    const changeDir = worktreePath ? findChangeDir(worktreePath, issue.number) : null;
    return changeDir ? path.join(changeDir, 'tasks.json') : undefined;
  };

  const retryIssueCheckpoint = (
    projectId: string,
    issue: Issue,
    startedBy: 'retry' | 'retry-checkpoint',
  ): void => {
    const issueRepo = stateManager.getIssueRepo();
    issueRepo.updateRetryCount(issue.id, 0);
    issueRepo.updateBlockedReason(issue.id, null);
    issueRepo.updateStatus(issue.id, IssueStatus.Active);

    const workflowApplicationService = createWorkflowApplicationService();
    if (!workflowApplicationService) {
      return;
    }

    try {
      workflowApplicationService.retryStage({
        issueId: issue.id,
        stage: issue.stage,
        tasksPath: getIssueTasksPath(projectId, issue),
        startedBy,
      });
    } catch {
      // Legacy blocked issues may not have a recoverable WorkflowRun aggregate.
    }
  };

  const approveThroughWorkflowRun = (issue: Issue): boolean => {
    if (!issue.approvalState || !activeWorkflowRunExists(issue.id)) return false;
    createWorkflowApplicationService()?.approveStage({
      issueId: issue.id,
      stage: issue.approvalState.stage as Stage,
      approval: { output: issue.approvalState.output },
    });
    return true;
  };

  const rejectThroughWorkflowRun = (issue: Issue, output: unknown): boolean => {
    if (!issue.approvalState || !activeWorkflowRunExists(issue.id)) return false;
    createWorkflowApplicationService()?.rejectStage({
      issueId: issue.id,
      stage: issue.approvalState.stage as Stage,
      approval: { output },
    });
    return true;
  };

  const getCurrentProjectId = (): string | null => {
    return projectService.getCurrentId();
  };

  app.get('/', async (c) => {
    try {
      const projectId = c.req.query('projectId') || getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const stage = c.req.query('stage') as Stage | undefined;
      const label = c.req.query('label') as string | undefined;
      const priorityInput = c.req.query('priority') as string | undefined;
      const archived = c.req.query('archived') as string | undefined;
      const all = c.req.query('all') as string | undefined;

      const normalizedPriority = normalizePriority(priorityInput);
      if (priorityInput !== undefined && normalizedPriority === null) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      let issues: Issue[];
      if (archived === 'true') {
        issues = issueRepo.findAll({ projectId, archivedOnly: true });
      } else if (all === 'true') {
        issues = issueRepo.findAll({ projectId, includeArchived: true });
      } else if (stage) {
        issues = issueService.getByStage(projectId, stage).filter(issue => !issue.archivedAt);
      } else {
        issues = issueService.getByProject(projectId);
      }

      if (all !== 'true' && archived !== 'true') {
        issues = issues.filter(issue => !issue.archivedAt);
      }

      if (normalizedPriority) {
        issues = issues.filter(issue => issue.priority === normalizedPriority);
      }

      if (label) {
        issues = issues.filter(issue => issue.labels.includes(label));
      }

      const project = projectService.getById(projectId);
      const issuesWithProject = issues.map(issue => ({
        ...issue,
        projectName: project?.name || 'unknown'
      }));

      const response: ApiResponse = {
        success: true,
        data: issuesWithProject
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/', async (c) => {
    try {
      const { title, body, labels, priority, model, stageModels } = await c.req.json();

      if (!title) {
        const response: ApiResponse = {
          success: false,
          error: 'title is required'
        };
        return c.json(response, 400);
      }

      let normalizedPriority: Priority | undefined = undefined;
      if (priority !== undefined) {
        const normalized = normalizePriority(priority);
        if (normalized === null) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid priority'
          };
          return c.json(response, 400);
        }
        normalizedPriority = normalized;
      }

      if (model !== undefined && model !== null && (typeof model !== 'string' || !isValidModelId(model))) {
        return c.json({ success: false, error: 'Invalid model format. Expected provider/model.' } satisfies ApiResponse, 400);
      }

      if (stageModels !== undefined && stageModels !== null) {
        if (typeof stageModels !== 'object' || Array.isArray(stageModels)) {
          return c.json({ success: false, error: 'stageModels must be an object' } satisfies ApiResponse, 400);
        }
        for (const [key, value] of Object.entries(stageModels as Record<string, unknown>)) {
          if (typeof value !== 'string' || !isValidModelId(value)) {
            return c.json({ success: false, error: `Invalid model for stage "${key}". Expected provider/model format.` } satisfies ApiResponse, 400);
          }
        }
      }

      const projectId = getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project'
        };
        return c.json(response, 400);
      }

      const issue = issueService.create({ projectId, title, body, labels, priority: normalizedPriority, model: model ?? undefined, stageModels: stageModels ?? undefined });

      const response: ApiResponse<Issue> = {
        success: true,
        data: issue
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/archive-completed', async (c) => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const result = await issueService.archiveAllCompleted(projectId);
      return c.json({ success: true, data: { archived: result.count, skipped: result.skipped, message: result.message, skippedNumbers: result.skippedNumbers ?? [] } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/archive', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      const { cleanup } = await c.req.json().catch(() => ({ cleanup: true }));
      const result = await issueService.archive(projectId, number, { cleanup: cleanup !== false });
      return c.json({ success: true, data: { issue: result.issue, warning: result.warning, message: `Issue #${number} archived` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/unarchive', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!issue.archivedAt) {
        return c.json({ success: false, error: `Issue #${number} is not archived` } satisfies ApiResponse, 400);
      }

      const result = await issueService.unarchive(projectId, number);
      return c.json({ success: true, data: { issue: result, message: `Issue #${number} unarchived` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/merge-blocked', async (c) => {
    try {
      const projectId = c.req.query('projectId') || getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        return c.json({ success: false, error: 'IssueRepo not configured' } satisfies ApiResponse, 500);
      }

      const blockedIssues = issueRepo.findByMergeStates([MergeState.Blocked])
        .filter(issue => issue.projectId === projectId);

      const blockedEntries = blockedIssues.map(issue => {
        const queueEntry = mergeQueue?.getStatus().find(e => e.issueNumber === issue.number);
        return {
          issueNumber: issue.number,
          title: issue.title,
          conflictingFiles: queueEntry?.conflictingFiles ?? [],
          blockedAt: issue.updatedAt,
        };
      });

      return c.json({ success: true, data: blockedEntries } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/check-suite', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!checkSuiteRepo) {
        return c.json({ success: false, error: 'CheckSuiteRepo not configured' } satisfies ApiResponse, 500);
      }

      const checkSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
      return c.json({ success: true, data: checkSuite } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/executions', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!stageExecutionRepo) {
        return c.json({ success: false, error: 'StageExecutionRepo not configured' } satisfies ApiResponse, 500);
      }

      const executions = stageExecutionRepo.findByIssueId(issue.id);
      return c.json({ success: true, data: executions } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/workflow-run', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!workflowRunService) {
        return c.json({ success: false, error: 'WorkflowRunService not configured' } satisfies ApiResponse, 500);
      }

      const run = workflowRunService.getLatestRunForIssue(issue.id);
      if (!run) {
        return c.json({ success: false, error: `No active workflow run for issue #${number}` } satisfies ApiResponse, 404);
      }

      return c.json({
        success: true,
        data: projectWorkflowRun(run),
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/stage-state', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!stageStateService) {
        return c.json({ success: false, error: 'StageStateService not configured' } satisfies ApiResponse, 500);
      }

      if (workflowRunService) {
        const run = workflowRunService.getLatestRunForIssue(issue.id);
        if (run) {
          const stages = stageStateService.getIssueStageStateFromWorkflowRun(run);
          return c.json({
            success: true,
            data: {
              issueId: issue.id,
              issueNumber: issue.number,
              stages,
            },
          } satisfies ApiResponse);
        }
      }

      const stages = stageStateService.getIssueStageState(issue.id);

      return c.json({
        success: true,
        data: {
          issueId: issue.id,
          issueNumber: issue.number,
          stages,
        },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/merge-queue/status', async (c) => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      if (!mergeQueue) {
        return c.json({ success: true, data: { items: [] } } satisfies ApiResponse);
      }

      const entries = mergeQueue.getStatus().filter(e => e.projectId === projectId);
      return c.json({ success: true, data: { items: entries } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const comments = issueService.getCommentsByIssue(issue.id);
      const project = projectService.getById(projectId);

      const checkSuite = checkSuiteRepo ? checkSuiteRepo.findActiveByIssueId(issue.id) : null;

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
          projectName: project?.name || 'unknown',
          projectPath: project?.path || '',
          baseBranch: project?.baseBranch || 'main',
          comments,
          checkSuite
        }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.patch('/:number', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const { title, body, addLabels, removeLabels, priority, model, stageModels } = await c.req.json();

      if (model !== undefined && model !== null && (typeof model !== 'string' || !isValidModelId(model))) {
        return c.json({ success: false, error: 'Invalid model format. Expected provider/model.' } satisfies ApiResponse, 400);
      }

      if (stageModels !== undefined && stageModels !== null) {
        if (typeof stageModels !== 'object' || Array.isArray(stageModels)) {
          return c.json({ success: false, error: 'stageModels must be an object' } satisfies ApiResponse, 400);
        }
        for (const [key, value] of Object.entries(stageModels as Record<string, unknown>)) {
          if (typeof value !== 'string' || !isValidModelId(value)) {
            return c.json({ success: false, error: `Invalid model for stage "${key}". Expected provider/model format.` } satisfies ApiResponse, 400);
          }
        }
      }

      const updateData: Partial<{ title: string; body: string; labels: string[]; priority: Priority; model: string | null; stageModels: Record<string, string> | null }> = {};

      if (title !== undefined) updateData.title = title;
      if (body !== undefined) updateData.body = body;
      if (model !== undefined) updateData.model = model;
      if (stageModels !== undefined) updateData.stageModels = stageModels;

      if (priority !== undefined) {
        const normalized = normalizePriority(priority);
        if (normalized === null) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid priority'
          };
          return c.json(response, 400);
        }
        updateData.priority = normalized;
      }
      
      if (addLabels || removeLabels) {
        let currentLabels = [...issue.labels];
        
        if (addLabels && Array.isArray(addLabels)) {
          currentLabels = [...new Set([...currentLabels, ...addLabels])];
        }
        
        if (removeLabels && Array.isArray(removeLabels)) {
          currentLabels = currentLabels.filter(l => !removeLabels.includes(l));
        }
        
        updateData.labels = currentLabels;
      }

      issueService.update(issue.id, updateData);
      const updated = issueService.getByNumber(projectId, number);

      const response: ApiResponse<Issue> = {
        success: true,
        data: updated || undefined
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/comments', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { body } = await c.req.json();
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!body) {
        const response: ApiResponse = {
          success: false,
          error: 'body is required'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const comment = issueService.createComment(issue.id, body);

      const response: ApiResponse<Comment> = {
        success: true,
        data: comment
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.delete('/:number/comments/:commentId', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const commentId = c.req.param('commentId');
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const deleted = issueService.deleteComment(issue.id, commentId);
      if (!deleted) {
        const response: ApiResponse = {
          success: false,
          error: `Comment not found`
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: { message: `Deleted comment ${commentId} from issue #${number}` }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/start', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (issue.status === IssueStatus.Blocked) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is blocked. Use: mo issue retry ${number} or mo issue rerun ${number}`
        };
        return c.json(response, 400);
      }

      if (issue.status === IssueStatus.Closed) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is closed. Run: mo issue reopen ${number}`
        };
        return c.json(response, 400);
      }

      if (issue.status === IssueStatus.Paused) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is paused. Run: mo issue approve ${number} to resume`
        };
        return c.json(response, 400);
      }

      if (issue.stage !== Stage.Backlog) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not in a startable stage (current: ${issue.stage}). Only backlog issues can be started.`
        };
        return c.json(response, 400);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const result = agentRunner.enqueue(issue.id, 'start-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} enqueued for start-pipeline`,
        }
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/force-stop', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      agentRunner.cancelAll(issue.id);
      issueService.setStatus(issue.id, IssueStatus.Interrupted);

      const response: ApiResponse = {
        success: true,
        data: { ok: true as const, issueNumber: number }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/close', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (agentRunner) {
        const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
        if (queueStatus.running) {
          const response: ApiResponse = {
            success: false,
            error: `Issue #${number} has a task running. Wait for it to complete or force-stop first.`
          };
          return c.json(response, 409);
        }
      }

      const { cleanup } = await c.req.json().catch(() => ({ cleanup: false }));

      const closedIssue = issueService.close(projectId, number);
      if (!closedIssue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (cleanup && worktreeManager) {
        const project = projectService.getById(projectId);
        if (project) {
          await worktreeManager.remove(project.path, project.name, number).catch((err) => {
            log.warn('Failed to cleanup worktree on close', { number, error: err instanceof Error ? err.message : err });
          });
        }
      }

      const response: ApiResponse = {
        success: true,
        data: { issue: closedIssue, message: `Issue #${number} closed` }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/reopen', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.reopen(projectId, number);

      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found or not reopenable (only closed issues can be reopened)` } satisfies ApiResponse, 404);
      }

      const refreshedIssue = issueService.getByNumber(projectId, number);
      return c.json({
        success: true,
        data: {
          issue: refreshedIssue ?? issue,
          message: `Issue #${number} reopened at stage ${issue.stage}.`,
        }
      });
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/resume', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.status !== IssueStatus.Paused && issue.status !== IssueStatus.Interrupted) {
        return c.json({
          success: false,
          error: `Issue #${number} cannot be resumed (current status: ${issue.status}). Use retry, rerun, or rewind instead.`
        } satisfies ApiResponse, 409);
      }

      if (agentRunner) {
        agentRunner.recoverSingleIssueById(issue.id);
      }

      const resumedIssue = issueService.resume(projectId, number);
      if (!resumedIssue) {
        return c.json({ success: false, error: `Failed to resume issue #${number}` } satisfies ApiResponse, 500);
      }

      if (agentRunner) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
        return c.json({
          success: true,
          data: {
            issue: issueService.getByNumber(projectId, number),
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} resumed and enqueued for resume-pipeline`,
          }
        } satisfies ApiResponse, 202);
      }

      return c.json({
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          message: `Issue #${number} resumed at stage ${issue.stage}.`,
        }
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/cleanup', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);

      if (worktreeManager && project) {
        await worktreeManager.remove(project.path, project.name, issue.number);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} worktree cleaned up` }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/skip-to-review', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: false,
          error: `No OpenSpec Change found for issue #${number}. Use "mo propose ${number}" first.`
        };
        return c.json(response, 400);
      }

      issueService.transitionToStage(issue.id, Stage.Check);
      issueService.setStatus(issue.id, IssueStatus.Active);

      if (agentRunner) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

        const updatedIssue = issueService.getByNumber(projectId, number);
        const response: ApiResponse = {
          success: true,
          data: {
            issue: updatedIssue,
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} skipping to review stage. Change: ${change.changePath}`
          }
        };
        return c.json(response, 202);
      }

      const updatedIssue2 = issueService.getByNumber(projectId, number);

      const response2: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue2,
          message: `Issue #${number} stage set to check (no agent runner). Change: ${change.changePath}`
        }
      };
      return c.json(response2);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/approve', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'IssueRepo not configured'
        };
        return c.json(response, 500);
      }

      if (!isCurrentStageApproval(issue, issue.stage, 'awaiting')) {
        const pendingIssue = issueRepo.findPendingApprovalByIssueId(issue.id);
        if (!pendingIssue) {
          const response: ApiResponse = {
            success: false,
            error: `No pending approval for issue #${number}. The pipeline may have completed or not been started. Try: mo issue start ${number}`
          };
          return c.json(response, 400);
        }
      }

      const approvalStage = issue.approvalState?.stage;

      if (approvalStage === Stage.Check) {
        const approvalOutput = issue.approvalState?.output as Record<string, unknown> | undefined;
        const latestReviewPassed = getAuthoritativeCheckReviewPassed(issue.id, workflowRunService, stageExecutionRepo);
        const latestReviewPassedOutput = latestReviewPassed?.output as Record<string, unknown> | undefined;

        if (!latestReviewPassed || latestReviewPassed.status !== 'pass' || latestReviewPassedOutput?.verdict !== 'PASS') {
          return c.json({
            success: false,
            error: `Cannot approve: latest review verdict is '${latestReviewPassedOutput?.verdict ?? latestReviewPassed?.status ?? 'unknown'}', expected 'PASS'. Re-run checks or wait for completion.`
          } satisfies ApiResponse, 409);
        }

        if (typeof approvalOutput?.snapshotSha !== 'string' || approvalOutput.snapshotSha.length === 0) {
          return c.json({
            success: false,
            error: 'Cannot approve: approval snapshot is missing. Re-run checks to regenerate an authoritative review result.'
          } satisfies ApiResponse, 409);
        }

        if (typeof latestReviewPassedOutput?.snapshotSha !== 'string' || latestReviewPassedOutput.snapshotSha.length === 0) {
          return c.json({
            success: false,
            error: 'Cannot approve: latest review snapshot is missing. Re-run checks to regenerate an authoritative review result.'
          } satisfies ApiResponse, 409);
        }

        if (latestReviewPassedOutput.snapshotSha !== approvalOutput.snapshotSha) {
          return c.json({
            success: false,
            error: 'Cannot approve: approval snapshot does not match the latest authoritative review snapshot. The check state may have changed since approval was requested.'
          } satisfies ApiResponse, 409);
        }

        if (checkSuiteRepo) {
          const activeSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
          if (activeSuite) {
            const checks = activeSuite.checks as unknown as Record<string, { status?: string }>;
            const reviewPassedCheck = checks['review-passed'] ?? checks['ai-review'];
            if (reviewPassedCheck?.status !== 'passed') {
              return c.json({
                success: false,
                error: `Cannot approve: latest review verdict is '${reviewPassedCheck?.status || 'unknown'}', expected 'passed'. Re-run checks or wait for completion.`
              } satisfies ApiResponse, 409);
            }

            const mergeReadyCheck = checks['merge-ready'];
            if (mergeReadyCheck?.status !== 'passed') {
              return c.json({
                success: false,
                error: `Cannot approve: merge-ready is '${mergeReadyCheck?.status || 'unknown'}', expected 'passed'. Re-run checks or wait for completion.`
              } satisfies ApiResponse, 409);
            }

            const approvalSnapshotSha = approvalOutput.snapshotSha;
            if (typeof approvalSnapshotSha === 'string' && activeSuite.snapshotSha !== approvalSnapshotSha) {
              return c.json({
                success: false,
                error: 'Cannot approve: approval snapshot does not match active CheckSuite snapshot. The check state may have changed since approval was requested.'
              } satisfies ApiResponse, 409);
            }
          }
        }

        if (worktreeManager) {
          const project = projectService.getById(projectId);
          if (project) {
            const worktreePath = worktreeManager.getPath(project.name, issue.number);
            if (worktreePath) {
              try {
                const currentHead = await worktreeManager.getHeadSha(worktreePath);
                const approvalSnapshotSha = approvalOutput.snapshotSha;

                if (typeof approvalSnapshotSha === 'string' && currentHead !== approvalSnapshotSha) {
                  return c.json({
                    success: false,
                    error: 'Cannot approve: current HEAD does not match approval snapshot. The code may have changed since approval was requested.'
                  } satisfies ApiResponse, 409);
                }

                const isClean = await worktreeManager.isWorktreeClean(worktreePath);
                if (!isClean) {
                  return c.json({
                    success: false,
                    error: 'Cannot approve: worktree has uncommitted changes. Commit or stash changes before approving.'
                  } satisfies ApiResponse, 409);
                }
              } catch (err) {
                log.warn('Failed to validate worktree state for approval', { error: err });
              }
            }
          }
        }

        if (issue.approvalState) {
          if (!approveThroughWorkflowRun(issue)) {
            return c.json({
              success: false,
              error: `Cannot approve: issue #${number} has no active WorkflowRun. Re-run the pipeline so approval can be recorded through the workflow aggregate.`
            } satisfies ApiResponse, 409);
          }
        }

        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

        const response: ApiResponse = {
          success: true,
          data: {
            issue: issueService.getByNumber(projectId, number),
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} approved, enqueued for resume-pipeline to Integrate stage`,
          }
        };
        return c.json(response, 202);
      }

      // Plan stage: just set approval state and resume pipeline; runner will auto-advance
      if (approvalStage === Stage.Plan) {
        if (issue.approvalState) {
          if (!approveThroughWorkflowRun(issue)) {
            return c.json({
              success: false,
              error: `Cannot approve: issue #${number} has no active WorkflowRun. Re-run the pipeline so approval can be recorded through the workflow aggregate.`
            } satisfies ApiResponse, 409);
          }
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} approved, enqueued for resume-pipeline`,
        }
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/reject', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      const body = await c.req.json().catch(() => ({}));
      const message = body.message as string | undefined;

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!isCurrentStageApproval(issue, issue.stage, 'awaiting')) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not awaiting approval at current stage`
        };
        return c.json(response, 400);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const rejectQueueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
      if (rejectQueueStatus.running) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} has a running task. Wait for it to complete first.`
        };
        return c.json(response, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'IssueRepo not configured'
        };
        return c.json(response, 500);
      }

      if (message) {
        issueService.createComment(issue.id, message);
      }

      const rejectedStage = issue.approvalState!.stage;
      const rejectedThroughWorkflowRun = rejectThroughWorkflowRun(issue, message ?? issue.approvalState!.output ?? null);

      if (!rejectedThroughWorkflowRun) {
        return c.json({
          success: false,
          error: `Cannot reject: issue #${number} has no active WorkflowRun. Re-run the pipeline so rejection can be recorded through the workflow aggregate.`
        } satisfies ApiResponse, 409);
      }

      if (rejectedStage === Stage.Check) {
        if (checkpointRepo) {
          checkpointRepo.delete(issue.number, Stage.Check);
        }

        if (worktreeManager) {
          const project = projectService.getById(projectId);
          if (project) {
            const worktreePath = worktreeManager.getPath(project.name, issue.number);
            if (worktreePath) {
              const changeDir = findChangeDir(worktreePath, issue.number);
              if (changeDir) {
                for (const filename of ['review.md', 'review-self-check.md']) {
                  const artifactPath = path.join(changeDir, filename);
                  try {
                    if (fs.existsSync(artifactPath)) {
                      fs.unlinkSync(artifactPath);
                    }
                  } catch {}
                }
              }
            }
          }
        }

        if (checkSuiteRepo) {
          const activeSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
          if (activeSuite) {
            checkSuiteRepo.updateChecks(activeSuite.id, 'user-approval', {
              status: 'failed',
              output: { rejected: true, message: message || 'User rejected' },
              ranAt: new Date().toISOString(),
            });
            checkSuiteRepo.updateStatus(activeSuite.id, 'failed');
          }
        }
      }

      if (rejectedStage === Stage.Plan) {
        if (checkpointRepo) {
          checkpointRepo.delete(issue.number, 'plan');
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} rejected, pipeline restarted from ${rejectedStage === Stage.Check ? 'build' : 'plan'}`
        }
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/messages', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { message } = await c.req.json();
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!message || typeof message !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'message is required and must be a string'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      if (!agentRunner.isIssueAwaitingApproval(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Pipeline is not paused for issue #${number}`
        };
        return c.json(response, 409);
      }

      issueService.createComment(issue.id, message);

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Message sent to issue #${number}, agent resumed`
        }
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/diff', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const ctx = await resolveIssueComparisonContext(projectId, issue, projectService, worktreeManager);
      if (!ctx.available) {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: ctx.reason, message: ctx.message }
        };
        return c.json(response);
      }

      const diffArgs = ['diff', `${ctx.base}...${ctx.head}`];

      let numstatOutput: { stdout: string };
      let fullDiffOutput: { stdout: string };
      try {
        [numstatOutput, fullDiffOutput] = await Promise.all([
          execFileAsync('git', [...diffArgs, '--numstat'], { cwd: projectService.getById(projectId)!.path }),
          execFileAsync('git', diffArgs, { cwd: projectService.getById(projectId)!.path }),
        ]);
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'git_error' as const, message: 'Failed to load diff. Check that the branch has commits.' }
        };
        return c.json(response);
      }

      const numstatEntries = new Map<string, { additions: number; deletions: number; isBinary: boolean }>();
      for (const line of numstatOutput.stdout.trim().split('\n')) {
        if (!line.trim()) continue;
        const parts = line.split('\t');
        if (parts.length < 3) continue;
        const [addStr, delStr, filePath] = parts;
        const isBinary = addStr === '-' && delStr === '-';
        numstatEntries.set(filePath, {
          additions: isBinary ? 0 : parseInt(addStr, 10),
          deletions: isBinary ? 0 : parseInt(delStr, 10),
          isBinary,
        });
      }

      const diffByFile = new Map<string, string>();
      const fullDiff = fullDiffOutput.stdout;
      if (fullDiff.trim()) {
        const blocks = fullDiff.split(/(?=^diff --git )/m);
        for (const block of blocks) {
          if (!block.trim()) continue;
          const firstLine = block.split('\n')[0];
          const match = firstLine.match(/^diff --git a\/(.+?) b\/(.+)$/);
          if (match) {
            diffByFile.set(match[1], block);
            diffByFile.set(match[2], block);
          }
        }
      }

      const files: DiffFile[] = [];
      let totalAdditions = 0;
      let totalDeletions = 0;
      for (const [filePath, stats] of numstatEntries) {
        files.push({
          file: filePath,
          additions: stats.additions,
          deletions: stats.deletions,
          diff: stats.isBinary ? '' : (diffByFile.get(filePath) || ''),
          isBinary: stats.isBinary,
        });
        totalAdditions += stats.additions;
        totalDeletions += stats.deletions;
      }

      const data: IssueDiffResponse = {
        available: true,
        reason: null,
        base: ctx.base,
        head: ctx.head,
        mergeBase: ctx.mergeBase,
        ahead: ctx.ahead,
        behind: ctx.behind,
        canFastForward: ctx.canFastForward,
        comparison: 'merge-base',
        summary: {
          filesChanged: files.length,
          commits: 0,
          additions: totalAdditions,
          deletions: totalDeletions,
        },
        files,
      };

      const response: ApiResponse = {
        success: true,
        data,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/worktree-status', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      if (!worktreeManager) {
        const response: ApiResponse = {
          success: true,
          data: { exists: false }
        };
        return c.json(response);
      }

      const status = await worktreeManager.getWorktreeStatus(
        project.path,
        project.name,
        issue.number,
        project.baseBranch
      );

      const response: ApiResponse = {
        success: true,
        data: status
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/file-content', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const filePath = c.req.query('path');
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      if (!filePath) {
        return c.json({ success: false, error: 'path query parameter is required' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      const branchName = `mo/issue-${number}`;

      if (!worktreeManager || !worktreeManager.exists(project.name, issue.number)) {
        return c.json({ success: false, error: 'Workspace has been removed' } satisfies ApiResponse, 400);
      }

      const worktreePath = worktreeManager.getPath(project.name, issue.number);
      if (!worktreePath) {
        return c.json({ success: false, error: 'Worktree path not found' } satisfies ApiResponse, 400);
      }

      let baseExists = false;
      try {
        const revOutput = await execFileAsync('git', ['rev-parse', '--verify', `refs/heads/${project.baseBranch}`], { cwd: project.path });
        baseExists = revOutput.stdout.trim().length > 0;
      } catch {
        baseExists = false;
      }

      if (!baseExists) {
        return c.json({ success: false, error: `Base branch ${project.baseBranch} not found` } satisfies ApiResponse, 400);
      }

      const readContent = async (ref: string) => {
        try {
          const result = await execFileAsync('git', ['show', `${ref}:${filePath}`], { cwd: project.path });
          return result.stdout;
        } catch {
          return '';
        }
      };

      const [baseContent, headContent] = await Promise.all([
        readContent(project.baseBranch),
        readContent(branchName),
      ]);

      if (!baseContent && !headContent) {
        return c.json({ success: false, error: 'Failed to read file content from git' } satisfies ApiResponse, 500);
      }

      return c.json({
        success: true,
        data: { base: baseContent, head: headContent },
      });
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/commits', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const ctx = await resolveIssueComparisonContext(projectId, issue, projectService, worktreeManager);
      if (!ctx.available) {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: ctx.reason, message: ctx.message, commits: [] }
        };
        return c.json(response);
      }

      let logOutput: { stdout: string };
      let summaryNumstatOutput: { stdout: string };
      try {
        [logOutput, summaryNumstatOutput] = await Promise.all([
          execFileAsync(
            'git',
            ['log', `${ctx.base}..${ctx.head}`, '--date=iso-strict', '--numstat', '--format=%x1e%H%x00%h%x00%s%x00%an%x00%aI'],
            { cwd: projectService.getById(projectId)!.path }
          ),
          execFileAsync('git', ['diff', `${ctx.base}...${ctx.head}`, '--numstat'], { cwd: projectService.getById(projectId)!.path }),
        ]);
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'git_error' as const, message: 'Failed to load commits. Check that the branch has commits.', commits: [] }
        };
        return c.json(response);
      }

      const commits: CommitEntry[] = [];
      const rawOutput = logOutput.stdout.trim();

      if (rawOutput) {
        const entries = rawOutput.split('\x1e');
        for (const entry of entries) {
          const trimmed = entry.trim();
          if (!trimmed) continue;

          const [headerLine = '', ...numstatLines] = trimmed.split('\n');
          const nullParts = headerLine.split('\x00');
          if (nullParts.length < 5) continue;

          const [fullHash, shortHash, message, author, date] = nullParts;

          let filesChanged = 0;
          let additions = 0;
          let deletions = 0;
          const files: string[] = [];

          for (const line of numstatLines) {
            const l = line.trim();
            if (!l) continue;
            const parts = l.split('\t');
            if (parts.length >= 3) {
              const [addStr, delStr, filePath] = parts;
              const isBinary = addStr === '-' && delStr === '-';
              files.push(filePath);
              if (!isBinary) {
                const add = parseInt(addStr, 10);
                const del = parseInt(delStr, 10);
                additions += add;
                deletions += del;
              }
            }
          }

          filesChanged = files.length;

          commits.push({
            hash: fullHash,
            shortHash,
            message,
            author,
            date,
            filesChanged,
            additions,
            deletions,
            files,
          });
        }
      }

      const summaryFiles = new Set<string>();
      for (const line of summaryNumstatOutput.stdout.trim().split('\n')) {
        if (!line.trim()) continue;
        const parts = line.split('\t');
        if (parts.length >= 3) {
          summaryFiles.add(parts[2]);
        }
      }

      const totalAdditions = commits.reduce((sum, c) => sum + c.additions, 0);
      const totalDeletions = commits.reduce((sum, c) => sum + c.deletions, 0);

      const data: IssueCommitsResponse = {
        available: true,
        reason: null,
        base: ctx.base,
        head: ctx.head,
        mergeBase: ctx.mergeBase,
        ahead: ctx.ahead,
        behind: ctx.behind,
        canFastForward: ctx.canFastForward,
        comparison: 'merge-base',
        summary: {
          filesChanged: summaryFiles.size,
          commits: commits.length,
          additions: totalAdditions,
          deletions: totalDeletions,
        },
        commits,
      };

      const response: ApiResponse = {
        success: true,
        data,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/commits/:hash/diff', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const hash = c.req.param('hash');
      const projectId = getCurrentProjectId();

      if (!/^[0-9a-f]{7,40}$/i.test(hash)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid commit hash'
        };
        return c.json(response, 400);
      }

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      const branchName = `mo/issue-${number}`;

      if (!worktreeManager) {
        const response: ApiResponse = {
          success: true,
          data: unavailableChangesData(issue, issue.stage === Stage.Backlog
            ? 'Issue has not started yet.'
            : 'Workspace has been removed.')
        };
        return c.json(response);
      }

      if (!worktreeManager.exists(project.name, issue.number)) {
        const response: ApiResponse = {
          success: true,
          data: unavailableChangesData(issue, issue.stage === Stage.Backlog
            ? 'Issue has not started yet.'
            : 'Workspace has been removed.')
        };
        return c.json(response);
      }

      let branchExists = false;
      try {
        const revOutput = await execFileAsync('git', ['rev-parse', '--verify', `refs/heads/${branchName}`], { cwd: project.path });
        branchExists = revOutput.stdout.trim().length > 0;
      } catch {
        branchExists = false;
      }

      if (!branchExists) {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'branch_missing' as const, message: `Branch ${branchName} not found.` }
        };
        return c.json(response);
      }

      let containsOutput: { stdout: string };
      try {
        containsOutput = await execFileAsync(
          'git',
          ['branch', '--contains', hash, '--list', branchName],
          { cwd: project.path }
        );
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'git_error' as const, message: 'Failed to verify commit belongs to branch.' }
        };
        return c.json(response);
      }

      if (!containsOutput.stdout.trim()) {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'branch_missing' as const, message: `Commit ${hash} does not belong to branch ${branchName}.` }
        };
        return c.json(response);
      }

      let diffOutput: { stdout: string };
      try {
        diffOutput = await execFileAsync(
          'git',
          ['show', '--format=', '--patch', hash],
          { cwd: project.path }
        );
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { available: false as const, reason: 'git_error' as const, message: 'Failed to load commit diff.' }
        };
        return c.json(response);
      }

      const data: CommitDiffResponse = {
        available: true,
        reason: null,
        hash,
        diff: diffOutput.stdout,
      };

      const response: ApiResponse = {
        success: true,
        data,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/logs', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!workflowLogRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'WorkflowLog not configured'
        };
        return c.json(response, 500);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const eventType = c.req.query('eventType') as string | undefined;
      const logs = workflowLogRepo.findByIssueId(issue.id, eventType);
      const entries = logs.map(log => ({
        id: log.id,
        eventType: log.eventType,
        data: (() => { try { return JSON.parse(log.data); } catch { return log.data; } })(),
        createdAt: log.createdAt,
      }));

      const response: ApiResponse = {
        success: true,
        data: entries
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/checkpoint', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!checkpointRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'PipelineCheckpointRepo not configured'
        };
        return c.json(response, 500);
      }

      const stages: string[] = ['plan', 'build'];
      const data = stages
        .map(stage => checkpointRepo!.get(issue.number, stage))
        .filter((cp): cp is NonNullable<typeof cp> => cp !== null)
        .map(cp => ({
          stage: cp.stage,
          completedSteps: cp.completedSteps,
          nextStep: cp.nextStep,
          updatedAt: cp.updatedAt,
        }));

      const response: ApiResponse = {
        success: true,
        data
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/coder-sessions', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!coderSessionRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'CoderSessionRepo not configured'
        };
        return c.json(response, 500);
      }

      const sessions = coderSessionRepo.findByIssueId(issue.id);
      const data = sessions
        .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
        .map(session => ({
        id: session.id,
        acpSessionId: session.acpSessionId,
        executionId: session.executionId,
        taskDescription: session.taskDescription,
        status: session.status,
        createdAt: session.createdAt,
        completedAt: session.completedAt,
        model: session.model,
        coderType: session.coderType,
        stage: session.stage,
        title: session.title,
        lastDataAt: session.lastDataAt,
        probeSentAt: session.probeSentAt,
        probeDeadlineAt: session.probeDeadlineAt,
        failureReason: session.failureReason,
      }));

      const response: ApiResponse = {
        success: true,
        data
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/coder-sessions/:sessionId', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const sessionId = c.req.param('sessionId');
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (!coderSessionRepo || !workflowLogRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'CoderSessionRepo or WorkflowLogRepo not configured'
        };
        return c.json(response, 500);
      }

      const session = coderSessionRepo.findById(sessionId);
      if (!session || session.issueId !== issue.id) {
        const response: ApiResponse = {
          success: false,
          error: `Coder session ${sessionId} not found`
        };
        return c.json(response, 404);
      }

      let streamEvents: SessionStreamLogEntry[] = [];
      if (sessionStreamLogRepo) {
        streamEvents = sessionStreamLogRepo.findBySessionId(session.acpSessionId);
      }

      let fallbackLogs: Array<{ id: string; sessionId: string; issueId: string; eventType: string; data: unknown; createdAt: string }> = [];
      if (streamEvents.length === 0) {
        const SESSION_STREAM_EVENT_TYPES = new Set([
          'agent_thought_chunk',
          'agent_message_chunk',
          'tool_call',
          'tool_call_update',
          'user_message_chunk',
          'mohist_prompt',
        ]);
        const rawFallbackLogs = workflowLogRepo.findBySessionId(session.acpSessionId);
        fallbackLogs = rawFallbackLogs
          .filter(l => SESSION_STREAM_EVENT_TYPES.has(l.eventType))
          .map(l => ({
            id: l.id,
            sessionId: session.acpSessionId,
            issueId: session.issueId,
            eventType: l.eventType,
            data: (() => { try { return JSON.parse(l.data); } catch { return l.data; } })(),
            createdAt: l.createdAt,
          }));
      }

      const transcriptEvents = streamEvents.length > 0 ? streamEvents : fallbackLogs.map(l => ({
        id: l.id,
        sessionId: l.sessionId,
        issueId: l.issueId,
        eventType: l.eventType,
        data: typeof l.data === 'string' ? l.data : JSON.stringify(l.data),
        createdAt: l.createdAt,
      }));
      const transcript = assembleSessionTranscript(session, transcriptEvents);
      const firstPromptEvent = transcriptEvents.find((event) => event.eventType === 'mohist_prompt');
      const firstPromptData = firstPromptEvent
        ? (() => { try { return JSON.parse(firstPromptEvent.data) as Record<string, unknown>; } catch { return {}; } })()
        : null;

      const terminalStatuses = new Set(['completed', 'failed', 'cancelled']);
      const isTerminal = terminalStatuses.has(session.status);
      const currentSessionState = (): string => {
        if (session.status === 'failed') return 'Session failed';
        if (session.status === 'probing') return 'Checking session';
        if (session.status === 'running') return 'Running';
        return 'No active session';
      };

      const data = {
        id: session.id,
        acpSessionId: session.acpSessionId,
        executionId: session.executionId,
        taskDescription: session.taskDescription,
        status: session.status,
        createdAt: session.createdAt,
        completedAt: session.completedAt,
        model: session.model,
        coderType: session.coderType,
        stage: session.stage,
        title: session.title,
        metadata: {
          sessionId: session.id,
          coderSessionId: session.id,
          issueId: session.issueId,
          acpSessionId: session.acpSessionId,
          executionId: session.executionId,
          title: session.title,
          status: session.status,
          currentSessionState: currentSessionState(),
          model: session.model,
          stage: session.stage,
          createdAt: session.createdAt,
          completedAt: isTerminal ? session.completedAt : null,
          cwd: projectService.getById(projectId)?.path ?? null,
          worktree: worktreeManager?.getPath(projectService.getById(projectId)?.name ?? '', issue.number) ?? null,
          firstPromptSentAt: typeof firstPromptData?.sentAt === 'string' ? firstPromptData.sentAt : null,
          lastActivityAt: transcript.session.lastActivityAt ?? null,
          lastDataAt: session.lastDataAt,
          probeSentAt: session.probeSentAt,
          probeDeadlineAt: session.probeDeadlineAt,
          failureReason: session.failureReason,
          eventCount: transcript.session.eventCount ?? null,
          toolCount: transcript.session.toolCount ?? null,
          turnCount: transcript.session.turnCount ?? null,
          changedFiles: transcript.session.changedFiles ?? null,
          warnings: transcript.session.warnings ?? null,
          hasUnknownTools: transcript.session.hasUnknownTools ?? null,
        },
        turns: transcript.turns,
        incomplete: transcript.incomplete,
        workflowLogs: fallbackLogs.length > 0 ? fallbackLogs : undefined,
      };

      const response: ApiResponse = {
        success: true,
        data
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/build-status', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 400);
      }

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: true,
          data: {
            stage: issue.stage,
            status: issue.stage === Stage.Build ? 'running' : (issue.stage === Stage.Done ? 'completed' : 'pending'),
            progress: { completed: 0, failed: 0, total: 0, currentTask: null },
            tasks: [],
          }
        };
        return c.json(response);
      }

      let tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null; durations?: number[] }> = [];
      let total = 0;
      let completed = 0;
      let failed = 0;
      let currentTask: string | null = null;

      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent) as { tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null; durations?: number[] }> };
        tasks = tasksFile.tasks;
        total = tasks.length;
        completed = tasks.filter(t => t.passes).length;
        failed = tasks.filter(t => t.error && !t.passes).length;
        const pending = tasks.find(t => !t.passes);
        currentTask = pending ? pending.id : null;
      } catch {
        log.warn('Failed to read tasks for build-status', { tasksPath: change.tasksPath, issueNumber: number });
      }

      let status: string;
      if (issue.stage === Stage.Done) {
        status = 'completed';
      } else if (issue.stage === Stage.Build) {
        status = 'running';
      } else if (completed === total && total > 0) {
        status = 'completed';
      } else {
        status = 'pending';
      }

      const response: ApiResponse = {
        success: true,
        data: {
          stage: issue.stage === Stage.Build ? 'build' : issue.stage,
          status,
          progress: { completed, failed, total, currentTask },
          tasks,
        }
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/:number/tasks', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 400);
      }

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: true,
          data: { version: 1, tasks: [] }
        };
        return c.json(response);
      }

      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent);
        const response: ApiResponse = {
          success: true,
          data: tasksFile
        };
        return c.json(response);
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { version: 1, tasks: [] }
        };
        return c.json(response);
      }
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/merge', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.stage !== Stage.Integrate) {
        return c.json({
          success: false,
          error: `Direct merge is not allowed: issue is in ${issue.stage} stage. Use Check approval to route through Integrate stage, or retry a blocked Integrate issue.`
        } satisfies ApiResponse, 409);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      return c.json({
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} direct merge bypass prevented — routed to Integrate stage via resume-pipeline`,
        },
      } satisfies ApiResponse, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/worktrees', async (c) => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project' } satisfies ApiResponse, 400);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (!worktreeManager) {
        return c.json({ success: false, error: 'WorktreeManager not configured' } satisfies ApiResponse, 500);
      }

      const worktrees = await worktreeManager.list(project.path);

      return c.json({ success: true, data: { worktrees } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number/queue', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;

      return c.json({
        success: true,
        data: {
          running: queueStatus.running ? {
            taskId: queueStatus.running.id,
            taskType: queueStatus.running.taskType,
            priority: queueStatus.running.priority,
            status: queueStatus.running.status,
            enqueuedAt: queueStatus.running.enqueuedAt,
            startedAt: queueStatus.running.startedAt,
          } : null,
          pending: queueStatus.pending.map(t => ({
            taskId: t.id,
            taskType: t.taskType,
            priority: t.priority,
            status: t.status,
            enqueuedAt: t.enqueuedAt,
            startedAt: t.startedAt,
          })),
          queueLength: queueStatus.queueLength,
        },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.delete('/:number/queue/:taskId', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const taskId = c.req.param('taskId');
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;

      const isRunningTask = queueStatus.running?.id === taskId;
      if (isRunningTask) {
        return c.json({ success: false, error: 'Cannot cancel a running task' } satisfies ApiResponse, 409);
      }

      const isPendingTask = queueStatus.pending.some(t => t.id === taskId);
      if (!isPendingTask) {
        return c.json({ success: false, error: `Task ${taskId} not found` } satisfies ApiResponse, 404);
      }

      const cancelled = agentRunner.cancel(taskId);
      if (!cancelled) {
        return c.json({ success: false, error: 'Failed to cancel task' } satisfies ApiResponse, 409);
      }

      return c.json({ success: true, data: { cancelled: true } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate, Stage.Done];

  app.post('/:number/rebase', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      const body = await c.req.json().catch(() => ({} as Record<string, unknown>));

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!REBASE_ALLOWED_STAGES.includes(issue.stage)) {
        return c.json({ success: false, error: `Rebase not available in current stage (${issue.stage})` } satisfies ApiResponse, 400);
      }

      if (issue.stage === Stage.Done) {
        if (!mergeQueue) {
          return c.json({ success: false, error: 'MergeQueue not configured' } satisfies ApiResponse, 500);
        }
        const retried = mergeQueue.retry(number);
        if (!retried) {
          return c.json({ success: false, error: `Issue #${number} is not in a retryable merge state` } satisfies ApiResponse, 409);
        }
        return c.json({ success: true, data: { rebased: true, message: 'Rebase delegated to merge queue retry' } } satisfies ApiResponse);
      }

      const workflowApplicationService = createWorkflowApplicationService();
      if (workflowApplicationService && activeWorkflowRunExists(issue.id)) {
        const project = projectService.getById(projectId);
        const worktreePath = project ? worktreeManager?.getPath(project.name, issue.number) : null;
        const changeDir = worktreePath ? findChangeDir(worktreePath, number) : null;
        const { run, decision } = workflowApplicationService.scheduleRebaseTask({
          issueId: issue.id,
          reason: body.reason as string | undefined,
          tasksPath: changeDir ? path.join(changeDir, 'tasks.json') : undefined,
          sessionId: null,
        });

        const rebaseTask = run.stageRuns.find(sr => sr.stage === issue.stage)?.tasks.find(t => t.id === 'rebase-branch');
        if (decision.nextWork.kind === 'failed') {
          return c.json({
            success: false,
            error: decision.nextWork.reason.message ?? 'Failed to schedule rebase task',
          } satisfies ApiResponse, 500);
        }

        return c.json({
          success: true,
          data: {
            taskId: 'rebase-branch',
            status: rebaseTask?.status ?? 'pending',
            message: `Rebase branch task scheduled for issue #${number}`,
          },
        } satisfies ApiResponse, 202);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const result = agentRunner.enqueue(issue.id, 'rebase', body);

      const response: ApiResponse = {
        success: true,
        data: {
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} enqueued for rebase`,
        }
      };
      return c.json(response, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });


  app.post('/:number/retry', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.status !== IssueStatus.Blocked) {
        return c.json({ success: false, error: `Issue #${number} is not blocked (current: ${issue.status}). Use retry only for failed or needs-action issues, or use rerun to re-execute the current stage.` } satisfies ApiResponse, 409);
      }

      const deliveryStatus = classifyMergeDelivery(issue);
      if (deliveryStatus === 'merged' || deliveryStatus === 'integrating') {
        return c.json({
          success: false,
          error: `Issue #${number} has already been merged or is in integrate stage. Automatic retry is disabled; manual intervention is required.`,
        } satisfies ApiResponse, 409);
      }

      let hasCheckpoint = false;
      let checkpointStage: string | null = null;

      if (worktreeManager && checkpointRepo) {
        const project = projectService.getById(projectId);
        if (project) {
          const wtPath = worktreeManager.getPath(project.name, number);
          if (wtPath) {
            const changeDir = findChangeDir(wtPath, number);
            if (changeDir) {
              const tasksPath = path.join(changeDir, 'tasks.json');
              if (fs.existsSync(tasksPath)) {
                hasCheckpoint = true;
                checkpointStage = issue.stage;
              }
            }
          }
        }
      }

      if (!hasCheckpoint) {
        return c.json({
          success: false,
          error: `Issue #${number} retry requires a checkpoint but none was found. Use rerun to re-execute the current stage, or use rewind (when available) to go to an earlier stage.`,
        } satisfies ApiResponse, 409);
      }

      retryIssueCheckpoint(projectId, issue, 'retry');

      if (agentRunner) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
        return c.json({
          success: true,
          data: {
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} retrying from checkpoint (${checkpointStage})`,
          },
        } satisfies ApiResponse, 202);
      }

      return c.json({
        success: true,
        data: { message: `Issue #${number} retrying from checkpoint (${checkpointStage})` },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/restart', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      return c.json({
        success: false,
        error: `restart has been removed; use retry, rerun, or rewind instead`,
      } satisfies ApiResponse, 410);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/rerun', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.stage === Stage.Backlog) {
        return c.json({ success: false, error: `Issue #${number} is in ${issue.stage} stage. Use start instead of rerun.` } satisfies ApiResponse, 400);
      }

      if (issue.stage === Stage.Done) {
        return c.json({ success: false, error: `Issue #${number} is in done stage. Rerun is not supported for completed issues.` } satisfies ApiResponse, 400);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (coderSessionRepo) {
        coderSessionRepo.failRunningByIssueId(issue.id);
      }

      if (checkpointRepo) {
        checkpointRepo.delete(issue.number, issue.stage);
      }

      if (issue.stage === Stage.Check && worktreeManager) {
        const worktreePath = worktreeManager.getPath(project.name, issue.number);
        if (worktreePath) {
          const changeDir = findChangeDir(worktreePath, issue.number);
          if (changeDir) {
            for (const filename of ['review.md', 'review-self-check.md']) {
              const artifactPath = path.join(changeDir, filename);
              try {
                if (fs.existsSync(artifactPath)) {
                  fs.unlinkSync(artifactPath);
                }
              } catch {}
            }
          }
        }
      }

      agentRunner.cancelAll(issue.id);

      const issueRepo = stateManager.getIssueRepo();
      clearApprovalEverywhere(issue.id, issue.stage);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const workflowApplicationService = createWorkflowApplicationService();
      if (workflowApplicationService) {
        const worktreePath = worktreeManager?.getPath(project.name, issue.number);
        const changeDir = worktreePath ? findChangeDir(worktreePath, issue.number) : null;
        const workflowOptions = {
          issueId: issue.id,
          stage: issue.stage,
          tasksPath: changeDir ? path.join(changeDir, 'tasks.json') : undefined,
          startedBy: 'rerun',
        };
        const decision = workflowApplicationService.resumeDecision(issue.id, workflowOptions);
        if (decision.nextWork.kind === 'failed') {
          workflowApplicationService.retryStage(workflowOptions);
        } else {
          workflowApplicationService.rerunStage(workflowOptions);
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      return c.json({
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} rerun from ${issue.stage} stage`,
        },
      } satisfies ApiResponse, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/retry-merge', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!mergeQueue) {
        return c.json({ success: false, error: 'MergeQueue not configured' } satisfies ApiResponse, 500);
      }

      const project = projectService.getById(projectId);
      if (project && worktreeManager && !worktreeManager.exists(project.name, issue.number)) {
        return c.json({ success: false, error: `No worktree found for issue #${number}` } satisfies ApiResponse, 404);
      }

      const retried = mergeQueue.retry(number);
      if (!retried) {
        const queueEntry = mergeQueue.getStatus().find(e => e.issueNumber === number);
        const currentState = queueEntry?.mergeState ?? 'unknown';
        return c.json({ success: false, error: `Issue #${number} is not in a retryable merge state (current state: ${currentState}; retryable: build-failed, conflict, blocked)` } satisfies ApiResponse, 409);
      }

      return c.json({ success: true, data: { message: `Issue #${number} re-enqueued for merge` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/check/retry-checkpoint', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.stage !== Stage.Check) {
        return c.json({ success: false, error: `Issue #${number} is not in check stage (current: ${issue.stage})` } satisfies ApiResponse, 409);
      }

      if (issue.status !== IssueStatus.Blocked) {
        return c.json({ success: false, error: `Issue #${number} is not blocked (current: ${issue.status}). Use retry only for blocked issues.` } satisfies ApiResponse, 409);
      }

      const stageState = stageStateService?.getIssueStageState(issue.id).find(s => s.stage === Stage.Check);
      const checkRepair = stageState?.checkRepair;
      if (checkRepair && !checkRepair.repairAvailable && checkRepair.attemptsRemaining === 0) {
        retryIssueCheckpoint(projectId, issue, 'retry-checkpoint');

        if (agentRunner) {
          const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
          return c.json({
            success: true,
            data: {
              taskId: result.taskId,
              status: result.status,
              queuePosition: result.queuePosition,
              message: `Retry checkpoint for issue #${number}: repair budget is exhausted, checkpoint will be retried without scheduling a new repair task`,
              repairBudgetExhausted: true,
            },
          } satisfies ApiResponse, 202);
        }

        return c.json({
          success: true,
          data: {
            message: `Retry checkpoint for issue #${number}: repair budget is exhausted, checkpoint will be retried without scheduling a new repair task`,
            repairBudgetExhausted: true,
          },
        } satisfies ApiResponse);
      }

      retryIssueCheckpoint(projectId, issue, 'retry-checkpoint');

      if (agentRunner) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
        return c.json({
          success: true,
          data: {
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Retry checkpoint for issue #${number}: checkpoint retry initiated`,
          },
        } satisfies ApiResponse, 202);
      }

      return c.json({
        success: true,
        data: {
          message: `Retry checkpoint for issue #${number}: checkpoint retry initiated`,
        },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/check/rerun-review', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.stage !== Stage.Check) {
        return c.json({ success: false, error: `Issue #${number} is not in check stage (current: ${issue.stage})` } satisfies ApiResponse, 409);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      if (coderSessionRepo) {
        coderSessionRepo.failRunningByIssueId(issue.id);
      }

      if (checkpointRepo) {
        checkpointRepo.delete(issue.number, Stage.Check);
      }

      const project = projectService.getById(projectId);
      if (worktreeManager && project) {
        const worktreePath = worktreeManager.getPath(project.name, issue.number);
        if (worktreePath) {
          const changeDir = findChangeDir(worktreePath, issue.number);
          if (changeDir) {
            for (const filename of ['review.md', 'review-self-check.md']) {
              const artifactPath = path.join(changeDir, filename);
              try {
                if (fs.existsSync(artifactPath)) {
                  fs.unlinkSync(artifactPath);
                }
              } catch {}
            }
          }
        }
      }

      agentRunner.cancelAll(issue.id);

      const issueRepo = stateManager.getIssueRepo();
      clearApprovalEverywhere(issue.id, Stage.Check);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const workflowApplicationService = createWorkflowApplicationService();
      if (workflowApplicationService && activeWorkflowRunExists(issue.id) && project) {
        const worktreePath = worktreeManager?.getPath(project.name, issue.number);
        const changeDir = worktreePath ? findChangeDir(worktreePath, issue.number) : null;
        const result = workflowApplicationService.rerunStage({
          issueId: issue.id,
          stage: Stage.Check,
          tasksPath: changeDir ? path.join(changeDir, 'tasks.json') : undefined,
          startedBy: 'rerun-review',
        });
        if (result.decision.nextWork.kind === 'failed') {
          workflowApplicationService.retryStage({
            issueId: issue.id,
            stage: Stage.Check,
            tasksPath: changeDir ? path.join(changeDir, 'tasks.json') : undefined,
            startedBy: 'rerun-review',
          });
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      return c.json({
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} rerunning review only (no repair task will be added)`,
        },
      } satisfies ApiResponse, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/check/repair-review-findings', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (issue.stage !== Stage.Check) {
        return c.json({ success: false, error: `Issue #${number} is not in check stage (current: ${issue.stage})` } satisfies ApiResponse, 409);
      }

      const workflowApplicationService = createWorkflowApplicationService();
      if (!workflowApplicationService) {
        return c.json({ success: false, error: 'WorkflowApplicationService not configured' } satisfies ApiResponse, 500);
      }

      const project = projectService.getById(projectId);
      const worktreePath = worktreeManager?.getPath(project?.name ?? '', issue.number);
      const changeDir = worktreePath ? findChangeDir(worktreePath, issue.number) : null;
      const tasksPath = changeDir ? path.join(changeDir, 'tasks.json') : undefined;

      const result = workflowApplicationService.scheduleFixReviewFindings({
        issueId: issue.id,
        stage: Stage.Check,
        tasksPath,
        startedBy: 'fix-review-findings',
      });

      switch (result.repairStatus) {
        case 'scheduled':
          stateManager.getIssueRepo().updateRetryCount(issue.id, 0);
          stateManager.getIssueRepo().updateBlockedReason(issue.id, null);
          stateManager.getIssueRepo().updateStatus(issue.id, IssueStatus.Active);

          if (agentRunner) {
            const queued = agentRunner.enqueue(issue.id, 'resume-pipeline');
            return c.json({
              success: true,
              data: {
                repairTaskId: result.repairTaskId,
                taskId: queued.taskId,
                status: queued.status,
                queuePosition: queued.queuePosition,
                message: `Fix review findings scheduled for issue #${number}`,
              },
            } satisfies ApiResponse, 202);
          }

          return c.json({
            success: true,
            data: {
              repairTaskId: result.repairTaskId,
              message: `Fix review findings scheduled for issue #${number}`,
            },
          } satisfies ApiResponse, 202);
        case 'already-running':
          stateManager.getIssueRepo().updateRetryCount(issue.id, 0);
          stateManager.getIssueRepo().updateBlockedReason(issue.id, null);
          stateManager.getIssueRepo().updateStatus(issue.id, IssueStatus.Active);

          return c.json({
            success: true,
            data: {
              repairTaskId: result.repairTaskId,
              message: `Fix review findings already in progress for issue #${number}`,
            },
          } satisfies ApiResponse, 200);
        case 'exhausted':
          return c.json({
            success: false,
            error: `Repair budget exhausted for issue #${number}. All automatic repair attempts have been used. Use 'Rerun review only' after making code changes.`,
          } satisfies ApiResponse, 409);
        case 'not-check-stage':
          return c.json({
            success: false,
            error: `Issue #${number} is not in check stage`,
          } satisfies ApiResponse, 409);
        case 'not-available':
          return c.json({
            success: false,
            error: `Fix review findings is only available after the Check review has failed, or while an existing repair task is pending or running.`,
          } satisfies ApiResponse, 409);
      }
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  return app;
}
