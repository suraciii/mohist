import type { StageContext, StageTaskResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';

export interface RebaseTaskOutput {
  rebased: boolean;
  baseBranch: string;
  beforeBaseSha: string;
  afterBaseSha: string;
  beforeHeadSha: string;
  afterHeadSha: string;
  shaChanged: boolean;
  conflicts: string[];
}

async function getBaseSha(worktreePath: string, baseBranch: string): Promise<string> {
  const { execFile } = await import('child_process');
  const { promisify } = await import('util');
  const execFileAsync = promisify(execFile);
  const { stdout } = await execFileAsync('git', ['merge-base', 'HEAD', baseBranch], { cwd: worktreePath });
  return stdout.trim();
}

async function rebaseServiceFn(ctx: StageContext): Promise<RebaseTaskOutput> {
  const project = ctx.projectRepo?.findById(ctx.issue.projectId);
  if (!project) {
    throw new Error(`Project not found: ${ctx.issue.projectId}`);
  }

  const worktreePath = ctx.worktreeManager.getPath(project.name, ctx.issue.number);
  if (!worktreePath) {
    throw new Error(`Worktree not found for issue #${ctx.issue.number}`);
  }

  const baseBranch = project.baseBranch;

  const beforeHeadSha = await ctx.worktreeManager.getHeadSha(worktreePath);
  const beforeBaseSha = await getBaseSha(worktreePath, baseBranch);

  const canFF = await ctx.worktreeManager.canFastForward(
    project.path,
    project.name,
    ctx.issue.number,
    baseBranch,
  );

  if (canFF) {
    return {
      rebased: false,
      baseBranch,
      beforeBaseSha,
      afterBaseSha: beforeBaseSha,
      beforeHeadSha,
      afterHeadSha: beforeHeadSha,
      shaChanged: false,
      conflicts: [],
    };
  }

  const rebaseResult = await ctx.worktreeManager.rebaseOntoMaster(
    project.path,
    project.name,
    ctx.issue.number,
    baseBranch,
    { abortOnConflict: false },
  );

  const afterHeadSha = await ctx.worktreeManager.getHeadSha(worktreePath);
  const afterBaseSha = await getBaseSha(worktreePath, baseBranch);
  const shaChanged = beforeHeadSha !== afterHeadSha;

  return {
    rebased: rebaseResult.success,
    baseBranch,
    beforeBaseSha,
    afterBaseSha,
    beforeHeadSha,
    afterHeadSha,
    shaChanged,
    conflicts: rebaseResult.conflicts,
  };
}

export async function executeRebaseBranchTask(
  ctx: StageContext,
  attempt: number,
  options?: { taskId?: string; title?: string },
): Promise<StageTaskResult> {
  const taskId = options?.taskId ?? 'rebase-branch';
  const title = options?.title ?? 'Rebase branch';
  const startedAt = Date.now();

  emitStageTaskUpdate(
    ctx.eventBus,
    ctx.issue.id,
    ctx.issue.projectId,
    ctx.issue.stage,
    taskId,
    title,
    'started',
    attempt,
    [],
  );

  try {
    const result = await rebaseServiceFn(ctx);
    const duration = Date.now() - startedAt;

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      ctx.issue.stage,
      taskId,
      title,
      'completed',
      attempt,
      [],
    );

    return {
      taskId,
      title,
      status: result.conflicts.length > 0 && !result.rebased ? 'failed' : 'completed',
      artifacts: [],
      attempts: attempt,
      duration,
      output: {
        kind: 'rebase-branch',
        stage: ctx.issue.stage,
        attempt,
        ...result,
      },
      reason:
        result.conflicts.length > 0 && !result.rebased
          ? `Rebase conflict: ${result.conflicts.join(', ')}`
          : result.rebased
            ? `Rebase completed; shaChanged=${result.shaChanged}`
            : 'Branch is up to date',
    };
  } catch (err) {
    const duration = Date.now() - startedAt;
    const error = err instanceof Error ? err.message : String(err);

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      ctx.issue.stage,
      taskId,
      title,
      'failed',
      attempt,
      [],
    );

    return {
      taskId,
      title,
      status: 'failed',
      artifacts: [],
      attempts: attempt,
      duration,
      reason: error,
      output: {
        kind: 'rebase-branch',
        stage: ctx.issue.stage,
        attempt,
        success: false,
        error,
      },
    };
  }
}
