import { Hono } from 'hono';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment, Priority, normalizePriority } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import type { ConflictResolutionDeps } from '../services/conflict-resolution';
import { WorktreeManager } from '../git/worktree-manager';
import type { LlmConfig } from '../agent-runtime';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { SessionStreamLogRepo, type SessionStreamLogEntry } from '../db/session-stream-log-repo';
import { CoderSessionRepo } from '../db/coder-session-repo';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { CheckSuiteRepo } from '../db/check-suite-repo';
import { StageExecutionRepo } from '../db/stage-execution-repo';

import { execFile } from 'child_process';
import { promisify } from 'util';
import { execFileSync } from 'child_process';
import { Log } from '../util/log';
import type { IssueQueueStatus } from '../services/agent-runner-service';
import { assembleSessionTranscript } from '../services/session-transcript-service';

import type { IssuePrerequisiteService, IssuePrerequisiteSummary, IssueStartEligibility } from '../services/issue-prerequisite-service';
import type { EpicService } from '../services/epic-service';

import type { WorkflowRunService } from '../services/workflow-run-service';

import type { MergeabilitySnapshot } from '../git/worktree-manager';


import { isValidModelId } from '../config/model-resolution';
import { classifyMergeDelivery } from '../workflow/issue-lifecycle';

type StageStateService = any;
const WorkflowApplicationService: any = class {};
type BaseDriftState = any;
type CandidateEvidence = any;
type WorkflowFacts = any;
type RebaseTaskOutput = any;
type BaseDriftInput = any;
type WorkflowRunSnapshot = any;
type IncompleteStageCompletionGuard = any;

const evaluateBaseDrift = (_i: any) => null as any;

type ChangesUnavailableReason = 'worktree_removed' | 'branch_missing' | 'not_started' | 'git_error';

function hasStageCompletionGuardDetails(error: unknown): error is { message: string; details: { stageCompletionGuard: IncompleteStageCompletionGuard } } {
  if (!error || typeof error !== 'object' || !('details' in error)) return false;
  const details = (error as { details?: unknown }).details;
  if (!details || typeof details !== 'object' || !('stageCompletionGuard' in details)) return false;
  const guard = (details as { stageCompletionGuard?: unknown }).stageCompletionGuard;
  return !!guard
    && typeof guard === 'object'
    && 'complete' in guard
    && (guard as { complete?: unknown }).complete === false
    && 'reason' in guard
    && typeof (guard as { reason?: unknown }).reason === 'string';
}

const STAGE_ALIASES: Record<string, Stage[]> = {
  active: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
};

const PIPELINE_STAGES = new Set<Stage>([Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate]);

type StageSelector = {
  matches: StagePredicate;
};

type StagePredicate = (issue: Issue) => boolean;

type StageSelectionResult =
  | { selectors: StageSelector[] }
  | { error: string };

function parseStageSelection(input: string | undefined): StageSelectionResult {
  if (!input) return { selectors: [] };
  const parts = input.split(',').map(s => s.trim()).filter(Boolean);
  if (parts.length === 0) return { selectors: [] };

  const selectors: StageSelector[] = [];

  for (const part of parts) {
    const lower = part.toLowerCase();
    if (lower === 'active') {
      selectors.push({
        matches: issue => PIPELINE_STAGES.has(issue.stage) && !isTerminalStatus(issue.status),
      });
    } else if (STAGE_ALIASES[lower]) {
      for (const s of STAGE_ALIASES[lower]) {
        selectors.push({ matches: issue => issue.stage === s });
      }
    } else if (Object.values(Stage).map(s => s.toLowerCase()).includes(lower)) {
      const stage = Object.values(Stage).find(s => s.toLowerCase() === lower)!;
      selectors.push({ matches: issue => issue.stage === stage });
    } else {
      return { error: `Unknown stage or alias: "${part}". Valid stages: ${Object.values(Stage).join(', ')}. Aliases: ${Object.keys(STAGE_ALIASES).join(', ')}.` };
    }
  }
  return { selectors };
}

function isAttentionIssue(issue: Issue): boolean {
  if (issue.approvalState?.status === 'awaiting' && issue.approvalState.stage === issue.stage) {
    return true;
  }
  if (issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted) {
    return true;
  }
  const delivery = classifyMergeDelivery(issue);
  if (delivery === 'blocked' || delivery === 'build-failed' || delivery === 'conflict') {
    return true;
  }
  return false;
}

function isTerminalStatus(status: IssueStatus): boolean {
  return status === IssueStatus.Closed || status === IssueStatus.Completed;
}

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

function computeDriftStateForIssue(
  issue: Issue,
  projectId: string,
  baseBranch: string,
  worktreeManager: WorktreeManager | null,
  workflowRunService: WorkflowRunService | undefined,
  projectService: ProjectService,
): BaseDriftState | null {
  if (!worktreeManager || !workflowRunService) return null;

  const activeStages: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate];
  if (!activeStages.includes(issue.stage)) return null;

  const project = projectService.getById(projectId);
  if (!project) return null;

  const worktreePath = worktreeManager.getPath(project.name, issue.number);
  if (!worktreePath) return null;

  let currentBaseSha: string | null = null;
  let candidateHeadSha: string | null = null;
  let mergeBaseSha: string | null = null;

  try {
    const baseResult = execFileSync('git', ['rev-parse', baseBranch], { cwd: project.path, encoding: 'utf-8' });
    currentBaseSha = baseResult.trim() || null;
  } catch {
    currentBaseSha = null;
  }

  try {
    candidateHeadSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: worktreePath, encoding: 'utf-8' }).trim() || null;
  } catch {
    candidateHeadSha = null;
  }

  if (currentBaseSha && candidateHeadSha) {
    try {
      const mbResult = execFileSync('git', ['merge-base', baseBranch, `mo/issue-${issue.number}`], { cwd: project.path, encoding: 'utf-8' });
      mergeBaseSha = mbResult.trim() || null;
    } catch {
      mergeBaseSha = null;
    }
  }

  const run = null as any;
  const workflowFacts: WorkflowFacts = {
    workflowRun: run as WorkflowRunSnapshot | null,
    currentStage: run?.currentStage ?? null,
    isRunning: false,
    runningTaskId: null,
  };

  const rebaseTask = run?.stageRuns
    .flatMap((sr: any) => sr.tasks)
    .find((t: any) => t.taskId === 'rebase-branch' && t.status === 'completed');

  const rebaseTaskOutput: RebaseTaskOutput | null = rebaseTask?.output && typeof rebaseTask.output === 'object'
    ? (() => {
        const output = rebaseTask.output as Record<string, unknown>;
        const conflicts = Array.isArray(output.conflicts)
          ? output.conflicts.filter((c: unknown): c is string => typeof c === 'string')
          : undefined;
        return {
          beforeBaseSha: (output.beforeBaseSha as string) ?? '',
          afterBaseSha: (output.afterBaseSha as string) ?? '',
          beforeHeadSha: (output.beforeHeadSha as string) ?? '',
          afterHeadSha: (output.afterHeadSha as string) ?? '',
          shaChanged: Boolean(output.shaChanged),
          conflicts,
        };
      })()
    : null;

  const checkStage = run?.stageRuns.find((sr: any) => sr.stage === Stage.Check);
  const mergeReadyCheck = checkStage?.checks.find((c: any) => c.checkName === 'merge-ready');
  const reviewCheck = checkStage?.checks.find((c: any) => c.checkName === 'review-passed');

  const mergeReadySnapshot: MergeabilitySnapshot | null = mergeReadyCheck?.output && typeof mergeReadyCheck.output === 'object'
    ? {
        kind: 'merge-ready' as const,
        strategy: 'squash' as const,
        targetBranch: (mergeReadyCheck.output as Record<string, unknown>).targetBranch as string ?? baseBranch,
        baseSha: (mergeReadyCheck.output as Record<string, unknown>).baseSha as string ?? '',
        candidateHeadSha: (mergeReadyCheck.output as Record<string, unknown>).candidateHeadSha as string ?? '',
        mergeBaseSha: (mergeReadyCheck.output as Record<string, unknown>).mergeBaseSha as string ?? '',
        canMerge: (mergeReadyCheck.output as Record<string, unknown>).canMerge as boolean ?? false,
        conflictFiles: Array.isArray((mergeReadyCheck.output as Record<string, unknown>).conflictFiles)
          ? (mergeReadyCheck.output as Record<string, unknown>).conflictFiles as string[]
          : [],
        checkedAt: (mergeReadyCheck.output as Record<string, unknown>).checkedAt as string ?? new Date().toISOString(),
      }
    : null;

  const candidateEvidence: CandidateEvidence = {
    observedBaseSha: rebaseTaskOutput?.afterBaseSha ?? mergeReadySnapshot?.baseSha ?? null,
    mergeReadySnapshot,
    approvalSnapshot: issue.approvalState ? {
      status: issue.approvalState.status,
      output: issue.approvalState.output,
      requestedAt: issue.approvalState.requestedAt,
      respondedAt: issue.approvalState.respondedAt ?? null,
    } : null,
    rebaseTaskOutput,
    reviewCheckOutput: reviewCheck?.output ?? null,
    mergeReadyCheckOutput: mergeReadyCheck?.output ?? null,
  };

  const driftInput: BaseDriftInput = {
    projectId,
    issueId: issue.id,
    issueNumber: issue.number,
    baseBranch,
    gitFacts: { currentBaseSha, candidateHeadSha, mergeBaseSha },
    candidateEvidence,
    workflowFacts,
  };

  return evaluateBaseDrift(driftInput);
}

function buildDriftResponse(driftState: BaseDriftState | null): {
  drifted: boolean;
  decision: string | null;
  safeWindow: boolean | null;
  deferReason: string | null;
  staleEvidence: { review: boolean; mergeReady: boolean; approval: boolean } | null;
  observedBaseSha: string | null;
  currentBaseSha: string | null;
  candidateHeadSha: string | null;
  mergeBaseSha: string | null;
  conflicts: string[] | null;
  nextAction: string | null;
} {
  if (!driftState) {
    return {
      drifted: false,
      decision: null,
      safeWindow: null,
      deferReason: null,
      staleEvidence: null,
      observedBaseSha: null,
      currentBaseSha: null,
      candidateHeadSha: null,
      mergeBaseSha: null,
      conflicts: null,
      nextAction: null,
    };
  }

  let nextAction: string | null = null;
  if (driftState.decision === 'enqueue') {
    nextAction = 'Rebase is queued; Mohist will rebase at a safe window.';
  } else if (driftState.decision === 'suggest') {
    nextAction = 'Rebase recommended; run "mo issue rebase ' + driftState.baseBranch + '" when ready.';
  } else if (driftState.decision === 'needs-attention') {
    nextAction = 'Stale approval detected. Rebase or rerun checks before approving.';
  } else if (driftState.decision === 'defer') {
    nextAction = 'Rebase deferred until safe window (' + (driftState.deferReason ?? 'unknown reason') + ').';
  } else if (driftState.decision === 'skip' && driftState.drifted) {
    nextAction = 'Candidate is aligned with current base.';
  }

  return {
    drifted: driftState.drifted,
    decision: driftState.decision,
    safeWindow: driftState.safeWindow,
    deferReason: driftState.deferReason ?? null,
    staleEvidence: driftState.staleEvidence ?? null,
    observedBaseSha: driftState.observedBaseSha,
    currentBaseSha: driftState.currentBaseSha,
    candidateHeadSha: driftState.candidateHeadSha,
    mergeBaseSha: driftState.mergeBaseSha,
    conflicts: driftState.conflicts ?? null,
    nextAction,
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
  _checkpointRepo?: PipelineCheckpointRepo,
  _resolveConflictsDeps?: ConflictResolutionDeps,
  checkSuiteRepo?: CheckSuiteRepo,
  _stageExecutionRepo?: StageExecutionRepo,
  stageStateService?: StageStateService,
  workflowRunService?: WorkflowRunService,
  issuePrerequisiteService?: IssuePrerequisiteService,
  epicService?: EpicService,
): Hono {
  const app = new Hono();

  const createWorkflowApplicationService = (): InstanceType<typeof WorkflowApplicationService> | null => {
    if (!workflowRunService) return null;
    return new WorkflowApplicationService(workflowRunService.getDatabaseManager());
  };

  const filterByAttentionWithDefinition = (issues: Issue[]): Issue[] => (
    issues.filter(isAttentionIssue)
  );

  const getReconciledRecoveryProjection = (
    projectId: string,
    issue: Issue,
  ): { recovery: ReturnType<typeof WorkflowApplicationService.prototype.getRecoveryProjection> | null; issue: Issue } => {
    const workflowAppService = createWorkflowApplicationService();
    if (!workflowAppService) return { recovery: null, issue };

    const recovery = workflowAppService.getRecoveryProjection(issue.id);
    const refreshedIssue = issueService.getByNumber(projectId, issue.number) ?? issue;
    return { recovery, issue: refreshedIssue };
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

      const stageInput = c.req.query('stage') as string | undefined;
      const label = c.req.query('label') as string | undefined;
      const priorityInput = c.req.query('priority') as string | undefined;
      const archived = c.req.query('archived') as string | undefined;
      const all = c.req.query('all') as string | undefined;
      const attention = c.req.query('attention') as string | undefined;

      const normalizedPriority = normalizePriority(priorityInput);
      if (priorityInput !== undefined && normalizedPriority === null) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const stageResult = parseStageSelection(stageInput);
      if ('error' in stageResult) {
        const response: ApiResponse = {
          success: false,
          error: stageResult.error
        };
        return c.json(response, 400);
      }
      const { selectors } = stageResult;

      const issueRepo = stateManager.getIssueRepo();
      let issues: Issue[];

      if (archived === 'true') {
        issues = issueRepo.findAll({ projectId, archivedOnly: true });
      } else if (all === 'true') {
        issues = issueRepo.findAll({ projectId, includeArchived: true });
      } else {
        issues = issueRepo.findAll({ projectId });
      }

      if (all !== 'true' && archived !== 'true') {
        issues = issues.filter(issue => !issue.archivedAt);
      }

      if (selectors.length > 0) {
        issues = issues.filter(issue => selectors.some(selector => selector.matches(issue)));
      }

      if (normalizedPriority) {
        issues = issues.filter(issue => issue.priority === normalizedPriority);
      }

      if (label) {
        issues = issues.filter(issue => issue.labels.includes(label));
      }

      if (attention === 'true') {
        issues = filterByAttentionWithDefinition(issues);
      }

      const project = projectService.getById(projectId);

      let issuesWithPrerequisites = issues.map(issue => {
        const driftState = computeDriftStateForIssue(
          issue,
          projectId,
          project?.baseBranch || 'main',
          worktreeManager ?? null,
          workflowRunService,
          projectService,
        );
        return {
          ...issue,
          projectName: project?.name || 'unknown',
          ...buildDriftResponse(driftState),
        };
      });

      if (issuePrerequisiteService && issues.length > 0) {
        const prereqViews = issuePrerequisiteService.getPrerequisiteViews(projectId, issues);
        issuesWithPrerequisites = issues.map(issue => {
          const view = prereqViews.get(issue.id);
          const driftState = computeDriftStateForIssue(
            issue,
            projectId,
            project?.baseBranch || 'main',
            worktreeManager ?? null,
            workflowRunService,
            projectService,
          );
          return {
            ...issue,
            projectName: project?.name || 'unknown',
            ...buildDriftResponse(driftState),
            prerequisites: view?.prerequisites ?? [],
            startEligibility: view?.startEligibility ?? { startable: true, reason: 'ready' as const, waitingForDelivery: [] },
          };
        });
      }

      const response: ApiResponse = {
        success: true,
        data: issuesWithPrerequisites
      };
      return c.json(response);
    } catch (error) {
      if (hasStageCompletionGuardDetails(error)) {
        return c.json({
          success: false,
          error: error.message,
        } satisfies ApiResponse, 409);
      }
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

      const view = issuePrerequisiteService
        ? issuePrerequisiteService.getPrerequisiteView(projectId, issue)
        : { prerequisites: [], startEligibility: { startable: true, reason: 'ready' as const, waitingForDelivery: [] } };

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
          prerequisites: view.prerequisites,
          startEligibility: view.startEligibility,
        }
      };
      return c.json(response, 201);
    } catch (error) {
      if (hasStageCompletionGuardDetails(error)) {
        return c.json({
          success: false,
          error: error.message,
        } satisfies ApiResponse, 409);
      }
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

      const { recovery, issue: refreshedIssue } = getReconciledRecoveryProjection(projectId, issue);
      const comments = issueService.getCommentsByIssue(refreshedIssue.id);
      const project = projectService.getById(projectId);

      const checkSuite = checkSuiteRepo ? checkSuiteRepo.findActiveByIssueId(refreshedIssue.id) : null;

      let prerequisites: IssuePrerequisiteSummary[] = [];
      let startEligibility: IssueStartEligibility = { startable: true, reason: 'ready' as const, waitingForDelivery: [] };

      if (issuePrerequisiteService) {
        const view = issuePrerequisiteService.getPrerequisiteView(projectId, refreshedIssue);
        prerequisites = view.prerequisites;
        startEligibility = view.startEligibility;
      }

      let primaryEpic: { id: string; title: string; status: string; priority: string } | null = null;
      if (epicService) {
        primaryEpic = epicService.getIssueEpic(projectId, issue.id);
      }

      const driftState = computeDriftStateForIssue(
        refreshedIssue,
        projectId,
        project?.baseBranch || 'main',
        worktreeManager ?? null,
        workflowRunService,
        projectService,
      );
      const driftResponse = buildDriftResponse(driftState);

      let convergence;
      if (stageStateService) {
        let stageStates;
        if (workflowRunService) {
          const run = null as any;
          if (run) {
            stageStates = stageStateService.getIssueStageStateFromWorkflowRun(run);
          }
        }
        if (!stageStates) {
          stageStates = stageStateService.getIssueStageState(issue.id);
        }
        const currentStageState = stageStates.find((s: any) => s.stage === issue.stage);
        convergence = currentStageState?.convergence;
        if (!convergence) {
          const stageWithConvergence = stageStates.find((s: any) => s.convergence);
          convergence = stageWithConvergence?.convergence;
        }
      }

      const response: ApiResponse = {
        success: true,
        data: {
          ...refreshedIssue,
          projectName: project?.name || 'unknown',
          projectPath: project?.path || '',
          baseBranch: project?.baseBranch || 'main',
          comments,
          checkSuite,
          prerequisites,
          startEligibility,
          primaryEpic,
          convergence,
          ...driftResponse,
          recovery,
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
        data: updated ?? undefined
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

  app.post('/:number/prerequisites', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { prerequisiteNumber } = await c.req.json();
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      if (typeof prerequisiteNumber !== 'number') {
        return c.json({ success: false, error: 'prerequisiteNumber is required and must be a number' } satisfies ApiResponse, 400);
      }

      if (!issuePrerequisiteService) {
        return c.json({ success: false, error: 'IssuePrerequisiteService not configured' } satisfies ApiResponse, 500);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      const result = issuePrerequisiteService.declarePrerequisite(projectId, number, prerequisiteNumber);

      if ('error' in result) {
        return c.json({
          success: false,
          error: result.error,
          data: { reason: result.reason },
        } satisfies ApiResponse, 400);
      }

      return c.json({
        success: true,
        data: {
          issue: {
            ...issue,
            prerequisites: result.prerequisites,
            startEligibility: result.startEligibility,
          },
          message: `Issue #${number} now requires Issue #${prerequisiteNumber} to be delivered before start.`,
        },
      } satisfies ApiResponse, 200);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.delete('/:number/prerequisites/:prerequisiteNumber', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const prerequisiteNumber = parseInt(c.req.param('prerequisiteNumber'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      if (!issuePrerequisiteService) {
        return c.json({ success: false, error: 'IssuePrerequisiteService not configured' } satisfies ApiResponse, 500);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      const removed = issuePrerequisiteService.removePrerequisite(projectId, number, prerequisiteNumber);

      if (!removed) {
        return c.json({ success: false, error: `Prerequisite Issue #${prerequisiteNumber} not found for Issue #${number}` } satisfies ApiResponse, 404);
      }

      const view = issuePrerequisiteService.getPrerequisiteView(projectId, issue);

      return c.json({
        success: true,
        data: {
          issue: {
            ...issue,
            prerequisites: view.prerequisites,
            startEligibility: view.startEligibility,
          },
          message: `Prerequisite #${prerequisiteNumber} removed from Issue #${number}.`,
        },
      } satisfies ApiResponse, 200);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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

  return app;
}
