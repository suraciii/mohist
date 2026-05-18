import * as fs from 'fs';
import * as path from 'path';
import { Stage, MergeState } from '../../types';
import type { StageContext, StageTaskResult, CheckResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import type { ExecutableTask, TaskKind } from './types';
import type { TaskExecutionKind } from '../domain';
import { executeRebaseBranchTask } from './rebase-task-handler';
import { createRepairFixAdapter, type RepairFixTaskId } from './repair-fix-adapter';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt } from '../../agents/artifact-prompt';
import { OpenSpecIntegrator } from '../../openspec/open-spec-integrator';

interface PlanTaskConfig {
  type: string;
  label: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: import('../../types').Issue, changeDir: string) => string;
}

export type DispatchableTask = ExecutableTask | {
  taskId: string;
  title: string;
  kind: 'agent-session' | 'service-call' | 'ralph-task';
  prompt?: string;
  cwd?: string;
  stage?: string;
  attempt?: number;
  artifactVerification?: (artifacts: string[]) => string[];
  serviceFn?: (ctx: StageContext) => Promise<unknown>;
  input?: unknown;
};

export interface TaskDispatchFactoryInput {
  ctx: StageContext;
  task: ExecutableTask;
  executionKind: TaskExecutionKind | TaskKind;
  attempt: number;
  failedCheck?: CheckResult;
  worktreePath: string;
}

export interface TaskDispatchFactoryRegistry {
  build(input: TaskDispatchFactoryInput): DispatchableTask | null;
}

export function createDefaultTaskDispatchFactoryRegistry(): TaskDispatchFactoryRegistry {
  const integrator = new OpenSpecIntegrator();
  return {
    build(input) {
      if (input.executionKind === 'rebase-task') return createRebaseDispatchTask(input);
      if (input.executionKind === 'repair-task') return createRepairDispatchTask(input);
      if (input.executionKind === 'service-call') return createServiceCallDispatchTask(input, integrator);
      if (input.executionKind === 'agent-session') return createAgentSessionDispatchTask(input);
      return { ...input.task, kind: 'ralph-task' };
    },
  };
}

function createPlanTaskConfigs(changeDir: string): PlanTaskConfig[] {
  return [
    {
      type: 'proposal',
      label: 'proposal.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'proposal.md')),
      buildPrompt: (issue, dir) => buildArtifactPrompt('proposal', issue, dir),
    },
    {
      type: 'specs',
      label: 'specs/',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'specs')),
      buildPrompt: (issue, dir) => buildArtifactPrompt('specs', issue, dir),
    },
    {
      type: 'design',
      label: 'design.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'design.md')),
      buildPrompt: (issue, dir) => buildArtifactPrompt('design', issue, dir),
    },
    {
      type: 'tasks',
      label: 'tasks.json',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'tasks.json')),
      buildPrompt: (issue, dir) => buildArtifactPrompt('tasks', issue, dir),
    },
    {
      type: 'self-review',
      label: 'self-review.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'self-review.md')),
      buildPrompt: (issue, dir) => buildSelfReviewPrompt(issue, dir),
    },
  ];
}

function createRebaseDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: async () => {
      const result = await executeRebaseBranchTask(input.ctx, input.attempt);
      if (result.status !== 'completed') {
        throw new Error(result.reason ?? 'Rebase branch failed');
      }
      return result.output;
    },
  };
}

function createRepairDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: async () => {
      const adapter = createRepairFixAdapter();
      const result = await adapter.dispatch(normalizeRepairTaskId(input.task.taskId), input.ctx, {
        worktreePath: input.worktreePath,
        failedCheck: defaultFailedCheckForTask(input.task.taskId, input.failedCheck),
        attempt: input.attempt,
      });
      if (result.status !== 'completed') {
        throw new Error(result.reason ?? `${input.task.title} failed`);
      }
      return result.output;
    },
  };
}

function createServiceCallDispatchTask(input: TaskDispatchFactoryInput, integrator: OpenSpecIntegrator): DispatchableTask | null {
  if (input.task.taskId === 'check:converge-review-snapshot') {
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => {
        const result = await executeConvergeReviewSnapshotTask(input.ctx);
        if (result.status !== 'completed') {
          throw new Error(result.reason ?? 'Converge review snapshot failed');
        }
        return result.output;
      },
    };
  }

  if (input.ctx.issue.stage !== Stage.Integrate) return null;
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: buildIntegrateServiceFn(input.task.taskId, input.worktreePath, integrator),
  };
}

function createAgentSessionDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask | null {
  if (input.ctx.issue.stage === Stage.Plan) return createPlanAgentSessionDispatchTask(input);
  if (input.ctx.issue.stage === Stage.Check && input.task.taskId === 'ai-review') return createCheckAiReviewDispatchTask(input);
  return input.task;
}

function createPlanAgentSessionDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const tasks = createPlanTaskConfigs(changeDir);
  const taskConfig = tasks.find(candidate => candidate.type === input.task.taskId);
  if (!taskConfig) throw new Error(`Unknown Plan task: ${input.task.taskId}`);

  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'plan');
  if (completedSteps.includes(taskConfig.type) && taskConfig.verifyArtifact()) {
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, []);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ restoredFromCheckpoint: true }),
    };
  }
  if (taskConfig.verifyArtifact()) {
    input.ctx.checkpointManager.markStepComplete(input.ctx.issue.number, 'plan', taskConfig.type, tasks[tasks.indexOf(taskConfig) + 1]?.type ?? null);
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, [taskConfig.label]);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ artifacts: [taskConfig.label], restoredFromDisk: true }),
    };
  }

  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt: taskConfig.buildPrompt(input.ctx.issue, changeDir),
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: 'plan',
    attempt: input.attempt,
    artifactVerification: () => taskConfig.verifyArtifact() ? [taskConfig.label] : [],
  };
}

function createCheckAiReviewDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const reviewOutputPath = 'review.md';
  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'check');
  if (completedSteps.includes(input.task.taskId) && fs.existsSync(path.join(changeDir, reviewOutputPath))) {
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, []);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ restoredFromCheckpoint: true }),
    };
  }

  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt: buildReviewerPrompt(input.ctx.issue, changeDir),
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: 'check',
    attempt: input.attempt,
    artifactVerification: () => fs.existsSync(path.join(changeDir, reviewOutputPath)) ? [reviewOutputPath] : [],
    input: { mode: 'ai-review' },
  };
}

function buildIntegrateServiceFn(taskId: string, worktreePath: string, integrator: OpenSpecIntegrator): (ctx: StageContext) => Promise<unknown> {
  if (taskId === 'integrate:spec-sync') {
    return async (ctx) => {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (!changeDir) throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
      const summary = await integrator.apply(changeDir, worktreePath);
      return {
        step: 'integrate:spec-sync' as const,
        capabilities: summary.capabilities,
        counts: { added: summary.added, modified: summary.modified, removed: summary.removed, renamed: summary.renamed },
        targetFiles: summary.targetFiles,
        conflicts: summary.conflicts,
        corrections: summary.corrections,
        valid: summary.valid,
        errors: summary.errors,
        mode: summary.mode,
      };
    };
  }

  if (taskId === 'integrate:archive-change') {
    return async (ctx) => {
      await ctx.artifactManager.archiveChange(ctx.issue.number);
      return { step: 'integrate:archive-change' as const, archivePath: ctx.artifactManager.getChangeDir(ctx.issue.number), success: true };
    };
  }

  if (taskId === 'integrate:merge') {
    return async (ctx) => {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      if (!project) throw new Error(`Project not found: ${ctx.issue.projectId}`);
      const baseBranch = project.baseBranch;

      if (ctx.issue.mergeState === MergeState.Merged) {
        return { step: 'integrate:merge' as const, targetBranch: baseBranch, skipped: true, reason: 'already-merged' };
      }
      if (!ctx.worktreeManager.mergeApprovedCandidate) throw new Error('worktreeManager.mergeApprovedCandidate is not available');

      const mergeTruth = await ctx.worktreeManager.mergeApprovedCandidate(project.path, project.name, ctx.issue.number, baseBranch);
      if ('failingStep' in mergeTruth) {
        throw new Error(`Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}` + (mergeTruth.conflictFiles?.length ? ` Conflicting files: ${mergeTruth.conflictFiles.join(', ')}` : ''));
      }
      if (ctx.issueRepo.setMergeState) ctx.issueRepo.setMergeState(ctx.issue.id, MergeState.Merged);
      return {
        step: 'integrate:merge' as const,
        targetBranch: mergeTruth.targetBranch,
        baseSha: mergeTruth.baseSha,
        candidateHeadSha: mergeTruth.candidateHeadSha,
        landedSha: mergeTruth.landedSha,
        rebased: mergeTruth.rebased,
      };
    };
  }

  throw new Error(`Unknown integrate task: ${taskId}`);
}

async function executeConvergeReviewSnapshotTask(ctx: StageContext): Promise<StageTaskResult> {
  const taskId = 'check:converge-review-snapshot';
  const title = 'Converge review snapshot';
  const startedAt = Date.now();
  emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'started', 1, []);

  try {
    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    const worktreePath = project ? ctx.worktreeManager.getPath(project.name, ctx.issue.number) : null;
    if (!worktreePath) throw new Error('Worktree not found');
    const convergence = await ctx.worktreeManager.createCheckConvergenceCommit(worktreePath, ctx.issue.number);
    if (!convergence.success) throw new Error(convergence.error ?? 'Convergence commit failed');

    const output = { converged: true, snapshotSha: convergence.headSha };
    const latestReviewPassed = getLatestCheckResultFromStage(ctx, 'review-passed');
    if (latestReviewPassed && ctx.checkSuiteRepo) {
      const { buildAuthoritativeAiReviewResult } = await import('../stage-context');
      const authoritative = buildAuthoritativeAiReviewResult(
        { ...latestReviewPassed, output: { ...((latestReviewPassed.output as Record<string, unknown>) ?? {}), snapshotSha: convergence.headSha } },
        { snapshotSha: convergence.headSha },
      );
      const suite = authoritative ? ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id) : null;
      if (suite && authoritative) {
        ctx.checkSuiteRepo.updateChecks(suite.id, 'review-passed', { status: 'passed', output: authoritative, ranAt: authoritative.convergedAt });
        ctx.checkSuiteRepo.updateSnapshotSha(suite.id, convergence.headSha);
      }
    }

    emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'completed', 1, []);
    return { taskId, title, status: 'completed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, output };
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'failed', 1, []);
    return { taskId, title, status: 'failed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, reason: message, output: { error: message } };
  }
}

function getLatestCheckResultFromStage(ctx: StageContext, checkName: string): { name: string; status: 'pass' | 'fail' | 'error' | 'pending'; output?: unknown } | undefined {
  const checkStage = ctx.workflowRun?.stageRuns.find(stageRun => stageRun.stage === ctx.issue.stage);
  const check = checkStage?.checks.find(candidate => candidate.checkName === checkName);
  if (!check) return undefined;
  return {
    name: check.checkName,
    status: check.status === 'passed' ? 'pass' : check.status === 'pending' || check.status === 'running' ? 'pending' : check.status === 'error' ? 'error' : 'fail',
    output: check.output ?? undefined,
  };
}

function normalizeRepairTaskId(taskId: string): RepairFixTaskId {
  const baseTaskId = taskId.replace(/:\d+$/, '');
  if (baseTaskId === 'fix-merge-readiness') return 'repair-merge';
  if (baseTaskId === 'fix-plan-review') return 'repair-plan-artifacts';
  return baseTaskId as RepairFixTaskId;
}

function defaultFailedCheckForTask(taskId: string, failedCheck?: CheckResult): CheckResult {
  if (failedCheck) return failedCheck;
  const baseTaskId = taskId.replace(/:\d+$/, '');
  const defaults: Record<string, CheckResult> = {
    'fix-build-health': { name: 'health:build', status: 'fail' },
    'fix-check-health': { name: 'health:check', status: 'fail' },
    'fix-integrate-health': { name: 'health:integrate', status: 'fail' },
    'fix-review-findings': { name: 'review-passed', status: 'fail', output: { verdict: 'FAIL' } },
    'fix-merge-readiness': { name: 'merge-ready', status: 'fail' },
    'fix-plan-review': { name: 'self-review-passed', status: 'fail' },
  };
  return defaults[baseTaskId] ?? { name: taskId, status: 'fail' };
}
