import * as path from 'path';
import { Stage } from '../types';
import type { StageContext } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import { Log } from '../util/log';
import { OpenSpecIntegrator } from '../openspec/open-spec-integrator';

const log = Log.create({ service: 'integrate-stage-runner' });

export interface IntegrateStageRunnerOptions {
  worktreePath: string;
}

interface IntegrateStepResult {
  step: 'integrate:spec-sync' | 'integrate:archive-change' | 'integrate:merge';
  status: 'completed' | 'failed';
  output: unknown;
  startedAt: string;
  completedAt: string;
  duration: number;
}

export class IntegrateStageRunner extends BaseStageRunner {
  private integrator: OpenSpecIntegrator;
  private worktreePath: string;

  constructor(options: IntegrateStageRunnerOptions) {
    super();
    this.integrator = new OpenSpecIntegrator();
    this.worktreePath = options.worktreePath;
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Integrate;
  }

  protected getPreTaskChecks(): import('./checks').Check[] {
    return [];
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const steps: IntegrateStepResult[] = [];

    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) {
      throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
    }

    const projectPath = this.worktreePath;

    const specSyncStartedAt = new Date().toISOString();
    let specSyncResult: IntegrateStepResult | undefined;

    try {
      log.info('Running spec sync', { issueNumber: ctx.issue.number, changeDir, projectPath });

      const summary = await this.integrator.apply(changeDir, projectPath);

      const specSyncOutput = {
        step: 'integrate:spec-sync' as const,
        capabilities: summary.capabilities,
        counts: {
          added: summary.added,
          modified: summary.modified,
          removed: summary.removed,
          renamed: summary.renamed,
        },
        targetFiles: summary.targetFiles,
        conflicts: summary.conflicts,
        valid: summary.valid,
        errors: summary.errors,
      };

      const duration = Date.now() - new Date(specSyncStartedAt).getTime();
      specSyncResult = {
        step: 'integrate:spec-sync',
        status: summary.valid ? 'completed' : 'failed',
        output: specSyncOutput,
        startedAt: specSyncStartedAt,
        completedAt: new Date().toISOString(),
        duration,
      };

      steps.push(specSyncResult);
      this.appendTaskResult(ctx, {
        taskId: 'integrate:spec-sync',
        title: 'Sync OpenSpec delta specs to main specs',
        status: summary.valid ? 'completed' : 'failed',
        artifacts: summary.targetFiles,
        attempts: 1,
        duration,
      });

      if (!summary.valid) {
        const errMsg = `Spec sync failed: ${summary.errors.join('; ')}`;
        log.warn('Spec sync failed', { issueNumber: ctx.issue.number, errors: summary.errors });
        throw new Error(errMsg);
      }

      log.info('Spec sync succeeded', { issueNumber: ctx.issue.number, capabilities: summary.capabilities });

} catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(specSyncStartedAt).getTime();

      if (!specSyncResult) {
        specSyncResult = {
          step: 'integrate:spec-sync',
          status: 'failed',
          output: { error: err instanceof Error ? err.message : String(err) },
          startedAt: specSyncStartedAt,
          completedAt,
          duration,
        };
      }
      if (!steps.includes(specSyncResult)) {
        steps.push(specSyncResult);
      }

      log.warn('Integrate stage failed at spec-sync', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }

    const archiveStartedAt = new Date().toISOString();
    let archiveResult: IntegrateStepResult | undefined;

    const changeName = path.basename(changeDir);
    const now = new Date();
    const datePrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    const expectedArchiveName = `${datePrefix}-${changeName}`;
    const expectedArchivePath = `openspec/changes/archive/${expectedArchiveName}`;

    try {
      log.info('Archiving OpenSpec change', { issueNumber: ctx.issue.number, changeDir });

      await ctx.artifactManager.archiveChange(ctx.issue.number);

      const duration = Date.now() - new Date(archiveStartedAt).getTime();

      archiveResult = {
        step: 'integrate:archive-change',
        status: 'completed',
        output: {
          step: 'integrate:archive-change' as const,
          archivePath: expectedArchivePath,
          success: true,
        },
        startedAt: archiveStartedAt,
        completedAt: new Date().toISOString(),
        duration,
      };

      steps.push(archiveResult);
      this.appendTaskResult(ctx, {
        taskId: 'integrate:archive-change',
        title: 'Archive OpenSpec change',
        status: 'completed',
        artifacts: [`openspec/changes/archive/${ctx.issue.number}`],
        attempts: 1,
        duration,
      });

      log.info('Archive succeeded', { issueNumber: ctx.issue.number });

    } catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(archiveStartedAt).getTime();

      if (!archiveResult) {
        archiveResult = {
          step: 'integrate:archive-change',
          status: 'failed',
          output: { error: err instanceof Error ? err.message : String(err) },
          startedAt: archiveStartedAt,
          completedAt,
          duration,
        };
      }
      if (!steps.includes(archiveResult)) {
        steps.push(archiveResult);
      }

      this.appendTaskResult(ctx, {
        taskId: 'integrate:archive-change',
        title: 'Archive OpenSpec change',
        status: 'failed',
        artifacts: [],
        attempts: 1,
        duration,
      });

      log.warn('Integrate stage failed at archive-change', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }

    const mergeStartedAt = new Date().toISOString();
    let mergeResult: IntegrateStepResult | undefined;

    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    const baseBranch = project?.baseBranch ?? 'main';

    try {
      log.info('Running integration merge', {
        issueNumber: ctx.issue.number,
        projectId: ctx.issue.projectId,
        baseBranch,
      });

      if (!ctx.worktreeManager.mergeApprovedCandidate) {
        throw new Error('worktreeManager.mergeApprovedCandidate is not available');
      }

      const mergeTruth = await ctx.worktreeManager.mergeApprovedCandidate(
        this.worktreePath,
        ctx.issue.projectId,
        ctx.issue.number,
        baseBranch
      );

      if ('failingStep' in mergeTruth) {
        const duration = Date.now() - new Date(mergeStartedAt).getTime();
        mergeResult = {
          step: 'integrate:merge',
          status: 'failed',
          output: {
            step: 'integrate:merge' as const,
            failingStep: mergeTruth.failingStep,
            targetBranch: mergeTruth.targetBranch,
            baseSha: mergeTruth.baseSha,
            candidateHeadSha: mergeTruth.candidateHeadSha,
            conflictFiles: mergeTruth.conflictFiles,
            error: mergeTruth.error,
          },
          startedAt: mergeStartedAt,
          completedAt: new Date().toISOString(),
          duration,
        };
        steps.push(mergeResult);
        this.appendTaskResult(ctx, {
          taskId: 'integrate:merge',
          title: 'Merge approved candidate to target branch',
          status: 'failed',
          artifacts: [],
          attempts: 1,
          duration,
        });

        log.warn('Integration merge failed', {
          issueNumber: ctx.issue.number,
          failingStep: mergeTruth.failingStep,
          error: mergeTruth.error,
          conflictFiles: mergeTruth.conflictFiles,
        });

        const err = new Error(
          `Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}` +
          (mergeTruth.conflictFiles?.length ? ` Conflicting files: ${mergeTruth.conflictFiles.join(', ')}` : '')
        );
        (err as any).mergeStep = mergeResult;
        throw err;
      }

      const duration = Date.now() - new Date(mergeStartedAt).getTime();
      mergeResult = {
        step: 'integrate:merge',
        status: 'completed',
        output: {
          step: 'integrate:merge' as const,
          targetBranch: mergeTruth.targetBranch,
          baseSha: mergeTruth.baseSha,
          candidateHeadSha: mergeTruth.candidateHeadSha,
          landedSha: mergeTruth.landedSha,
          fastForward: mergeTruth.fastForward,
          rebased: mergeTruth.rebased,
        },
        startedAt: mergeStartedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(mergeResult);
      this.appendTaskResult(ctx, {
        taskId: 'integrate:merge',
        title: 'Merge approved candidate to target branch',
        status: 'completed',
        artifacts: [mergeTruth.landedSha],
        attempts: 1,
        duration,
      });

      log.info('Integration merge succeeded', {
        issueNumber: ctx.issue.number,
        targetBranch: mergeTruth.targetBranch,
        baseSha: mergeTruth.baseSha,
        candidateHeadSha: mergeTruth.candidateHeadSha,
        landedSha: mergeTruth.landedSha,
        fastForward: mergeTruth.fastForward,
        rebased: mergeTruth.rebased,
      });

    } catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(mergeStartedAt).getTime();

      if (!mergeResult) {
        mergeResult = {
          step: 'integrate:merge',
          status: 'failed',
          output: { error: err instanceof Error ? err.message : String(err) },
          startedAt: mergeStartedAt,
          completedAt,
          duration,
        };
      }
      if (!steps.includes(mergeResult)) {
        steps.push(mergeResult);
      }

      log.warn('Integrate stage failed at merge', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }

    return {
      integrate: true,
      steps,
    };
  }

  protected getChecks(): import('./checks').Check[] {
    return [];
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }
}