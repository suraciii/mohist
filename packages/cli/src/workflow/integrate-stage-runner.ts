import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage, MergeState } from '../types';
import type { StageContext } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import { Log } from '../util/log';
import { OpenSpecIntegrator } from '../openspec/open-spec-integrator';
import { loadHealthGatePolicies, loadWorkflow } from './workflow-loader';
import { HealthGateCheck } from './checks/health-gate-check';
import type { CheckResult, StageTaskResult } from './stage-context';
import type { Check } from './checks';
import { createRepairFixAdapter } from './task-runtime/repair-fix-adapter';
import { executeRebaseBranchTask } from './task-runtime/rebase-task-handler';

const log = Log.create({ service: 'integrate-stage-runner' });
const execFileAsync = promisify(execFile);

interface MergeReadySnapshot {
  kind?: string;
  targetBranch?: string;
  strategy?: string;
  baseSha?: string;
  candidateHeadSha?: string;
  mergeBaseSha?: string;
  canMerge?: boolean;
  conflictFiles?: string[];
  checkedAt?: string;
  error?: string;
}

function isApprovedSnapshotFresh(
  approvedSnapshot: MergeReadySnapshot,
  baseBranch: string,
  currentBaseSha: string,
  currentCandidateHeadSha: string | null,
  currentMergeBaseSha: string | null,
): boolean {
  const snapshotBaseSha = String(approvedSnapshot.baseSha ?? '');
  const snapshotCandidateHeadSha = String(approvedSnapshot.candidateHeadSha ?? '');
  const snapshotMergeBaseSha = String(approvedSnapshot.mergeBaseSha ?? '');
  const snapshotTargetBranch = String(approvedSnapshot.targetBranch ?? '');

  if (snapshotTargetBranch !== baseBranch) return false;
  if (approvedSnapshot.canMerge !== true) return false;
  if (!snapshotBaseSha || !snapshotCandidateHeadSha || !snapshotMergeBaseSha) return false;
  if (currentCandidateHeadSha === null || currentMergeBaseSha === null) return false;

  return currentBaseSha === snapshotBaseSha
    && currentCandidateHeadSha === snapshotCandidateHeadSha
    && currentMergeBaseSha === snapshotMergeBaseSha;
}

async function resolveRefSha(repoPath: string, ref: string): Promise<string> {
  const { stdout } = await execFileAsync('git', ['rev-parse', ref], { cwd: repoPath });
  return stdout.trim();
}

function findArchivedChangePath(worktreePath: string, issueNumber: number): string | null {
  const archiveDir = path.join(worktreePath, 'openspec', 'changes', 'archive');
  if (!fs.existsSync(archiveDir)) {
    return null;
  }

  const pattern = `-${issueNumber}-`;
  const entries = fs.readdirSync(archiveDir, { withFileTypes: true });
  const matches = entries
    .filter(entry => entry.isDirectory() && entry.name.includes(pattern))
    .map(entry => path.join(archiveDir, entry.name))
    .sort();

  return matches.at(-1) ?? null;
}

export interface IntegrateStageRunnerOptions {
  worktreePath: string;
}

type IntegrateTaskId = 'integrate:spec-sync' | 'integrate:archive-change' | 'integrate:merge' | 'integrate:preflight';

interface IntegrateStepResult {
  step: IntegrateTaskId;
  status: 'completed' | 'failed';
  output: unknown;
  startedAt: string;
  completedAt: string;
  duration: number;
}

interface IntegrationContext {
  effectiveChangeDir: string;
  isAlreadyArchived: boolean;
  expectedArchivePath: string;
  projectPath: string;
}

export class IntegrateStageRunner extends BaseStageRunner {
  private integrator: OpenSpecIntegrator;
  private worktreePath: string;
  private integrateHealthGatePolicy: import('./workflow-loader').HealthGatePolicy;

  constructor(options: IntegrateStageRunnerOptions) {
    super();
    this.integrator = new OpenSpecIntegrator();
    this.worktreePath = options.worktreePath;
    const wf = loadWorkflow(this.worktreePath);
    this.integrateHealthGatePolicy = typeof wf === 'string'
      ? { enabled: true, command: 'npm run build', timeout: 300000, autoFix: false, maxFixAttempts: 0, fallbackReaction: { type: 'ask-user' } }
      : loadHealthGatePolicies(wf).postMerge;
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Integrate;
  }

  protected getPreTaskChecks(): import('./checks').Check[] {
    return [];
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const steps: IntegrateStepResult[] = [];

    this.emitIntegrationStarted(ctx);
    const integration = this.resolveIntegrationContext(ctx);

    const preflightResult = await this.validateMergeability(ctx, steps);
    if (!preflightResult.valid) {
      ctx.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'integrate:preflight',
        error: 'Merge preflight failed: the candidate cannot be cleanly squash-merged. Re-run Check to verify merge readiness.',
        output: steps[steps.length - 1]?.output,
      });
      return { integrate: false, steps };
    }

    const specSyncResult = await this.runSpecSyncStep(ctx, steps, {
      effectiveChangeDir: integration.effectiveChangeDir,
      isAlreadyArchived: integration.isAlreadyArchived,
      projectPath: integration.projectPath,
    });
    if (specSyncResult?.status === 'failed') {
      return { integrate: false, steps };
    }

    const archiveResult = await this.runArchiveStep(ctx, steps, {
      effectiveChangeDir: integration.effectiveChangeDir,
      isAlreadyArchived: integration.isAlreadyArchived,
      expectedArchivePath: integration.expectedArchivePath,
      projectPath: integration.projectPath,
    });
    if (archiveResult?.status === 'failed') {
      return { integrate: false, steps };
    }

    const mergeResult = await this.runMergeStep(ctx, steps);
    if (mergeResult?.status === 'failed') {
      return { integrate: false, steps };
    }

    const mergeStepOutput = mergeResult?.output as { targetBranch?: string; baseSha?: string; landedSha?: string } | undefined;

    ctx.emit('integration_completed', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      steps: steps.map(s => ({ step: s.step, status: s.status, output: s.output })),
    });

    if (mergeStepOutput?.targetBranch && mergeStepOutput?.landedSha && mergeStepOutput?.baseSha) {
      ctx.emit('base_branch_advanced', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        baseBranch: mergeStepOutput.targetBranch,
        newBaseSha: mergeStepOutput.landedSha,
        previousBaseSha: mergeStepOutput.baseSha,
      });
    }

    return {
      integrate: true,
      steps,
    };
  }

  private emitIntegrationStarted(ctx: StageContext): void {
    ctx.emit('integration_started', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
    });
  }

  private resolveIntegrationContext(ctx: StageContext): IntegrationContext {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    const archivedChangeDir = changeDir ? null : findArchivedChangePath(this.worktreePath, ctx.issue.number);
    const isAlreadyArchived = !changeDir && Boolean(archivedChangeDir);
    const effectiveChangeDir = changeDir ?? archivedChangeDir;
    if (!effectiveChangeDir) {
      throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
    }

    const changeName = isAlreadyArchived
      ? path.basename(effectiveChangeDir).replace(/^\d{4}-\d{2}-\d{2}-/, '')
      : path.basename(effectiveChangeDir);
    const now = new Date();
    const datePrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    const expectedArchiveName = `${datePrefix}-${changeName}`;

    return {
      effectiveChangeDir,
      isAlreadyArchived,
      expectedArchivePath: `openspec/changes/archive/${expectedArchiveName}`,
      projectPath: this.worktreePath,
    };
  }

  private async validateMergeability(
    ctx: StageContext,
    steps: IntegrateStepResult[],
  ): Promise<{ valid: true; snapshot: MergeReadySnapshot } | { valid: false; steps: IntegrateStepResult[] }> {
    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      const result = this.failedPreflightResult('project-not-found', 'unknown', null, null, null, 'Project not found');
      steps.push(result);
      return { valid: false, steps };
    }

    const baseBranch = project.baseBranch;
    const approvalOutput = ctx.issue.approvalState?.output as { mergeReadySnapshot?: MergeReadySnapshot } | undefined;
    const approvedSnapshot = approvalOutput?.mergeReadySnapshot;

    if (approvedSnapshot === null || approvedSnapshot === undefined) {
      log.info('No approved merge-ready snapshot found, will run preflight', {
        issueNumber: ctx.issue.number,
      });
    } else {
      const currentBaseSha = await resolveRefSha(project.path, baseBranch);
      const worktreePath = ctx.worktreeManager.getPath(project.name, ctx.issue.number);
      const currentCandidateHeadSha = worktreePath
        ? await ctx.worktreeManager.getHeadSha(worktreePath)
        : null;
      const currentMergeBaseSha = worktreePath
        ? await execFileAsync('git', ['merge-base', baseBranch, `mo/issue-${ctx.issue.number}`], { cwd: project.path })
            .then(result => result.stdout.trim())
            .catch(() => null)
        : null;

      if (isApprovedSnapshotFresh(approvedSnapshot, baseBranch, currentBaseSha, currentCandidateHeadSha, currentMergeBaseSha)) {
        const result: IntegrateStepResult = {
          step: 'integrate:preflight',
          status: 'completed',
          output: {
            step: 'integrate:preflight' as const,
            kind: approvedSnapshot.kind,
            strategy: approvedSnapshot.strategy,
            targetBranch: approvedSnapshot.targetBranch,
            baseSha: approvedSnapshot.baseSha,
            candidateHeadSha: approvedSnapshot.candidateHeadSha,
            mergeBaseSha: approvedSnapshot.mergeBaseSha,
            canMerge: approvedSnapshot.canMerge,
            conflictFiles: approvedSnapshot.conflictFiles,
            checkedAt: approvedSnapshot.checkedAt,
            refreshed: false,
          },
          startedAt: new Date().toISOString(),
          completedAt: new Date().toISOString(),
          duration: 0,
        };
        steps.push(result);
        return { valid: true, snapshot: approvedSnapshot as MergeReadySnapshot };
      }

      log.info('Approved merge-ready snapshot stale; Integrate requires Check rerun before delivery', {
        issueNumber: ctx.issue.number,
        snapshotTargetBranch: approvedSnapshot.targetBranch,
        currentBaseBranch: baseBranch,
        snapshotBaseSha: approvedSnapshot.baseSha,
        currentBaseSha,
        snapshotCandidateHeadSha: approvedSnapshot.candidateHeadSha,
        currentCandidateHeadSha,
        snapshotMergeBaseSha: approvedSnapshot.mergeBaseSha,
        currentMergeBaseSha,
      });

      const snapshot = ctx.worktreeManager.checkSquashMergeability
        ? await ctx.worktreeManager.checkSquashMergeability(
          project.path,
          project.name,
          ctx.issue.number,
          baseBranch,
        )
        : null;
      const result: IntegrateStepResult = {
        step: 'integrate:preflight',
        status: 'failed',
        output: {
          step: 'integrate:preflight' as const,
          kind: snapshot?.kind ?? approvedSnapshot.kind,
          strategy: snapshot?.strategy ?? approvedSnapshot.strategy,
          targetBranch: snapshot?.targetBranch ?? baseBranch,
          baseSha: snapshot?.baseSha ?? currentBaseSha,
          candidateHeadSha: snapshot?.candidateHeadSha ?? currentCandidateHeadSha,
          mergeBaseSha: snapshot?.mergeBaseSha ?? currentMergeBaseSha,
          canMerge: snapshot?.canMerge ?? false,
          conflictFiles: snapshot?.conflictFiles ?? [],
          checkedAt: snapshot?.checkedAt ?? new Date().toISOString(),
          refreshed: Boolean(snapshot),
          staleApprovedSnapshot: {
            targetBranch: approvedSnapshot.targetBranch,
            baseSha: approvedSnapshot.baseSha,
            candidateHeadSha: approvedSnapshot.candidateHeadSha,
            mergeBaseSha: approvedSnapshot.mergeBaseSha,
          },
          currentSnapshot: {
            targetBranch: baseBranch,
            baseSha: currentBaseSha,
            candidateHeadSha: currentCandidateHeadSha,
            mergeBaseSha: currentMergeBaseSha,
          },
          error: 'Approved merge-ready snapshot is stale. Re-run Check so the user can approve the current candidate before Integrate performs delivery side effects.',
        },
        startedAt: new Date().toISOString(),
        completedAt: new Date().toISOString(),
        duration: 0,
      };
      steps.push(result);
      if (snapshot) {
        ctx.emit('integration_preflight_refreshed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          status: 'failed',
          snapshot,
        });
      }
      return { valid: false, steps };
    }

    if (!ctx.worktreeManager.checkSquashMergeability) {
      const result = this.failedPreflightResult('preflight-unavailable', baseBranch, null, null, null, 'checkSquashMergeability is not available on worktreeManager');
      steps.push(result);
      return { valid: false, steps };
    }

    const snapshot = await ctx.worktreeManager.checkSquashMergeability(
      project.path,
      project.name,
      ctx.issue.number,
      baseBranch,
    );

    if (!snapshot.canMerge) {
      const result: IntegrateStepResult = {
        step: 'integrate:preflight',
        status: 'failed',
        output: {
          step: 'integrate:preflight' as const,
          kind: snapshot.kind,
          strategy: snapshot.strategy,
          targetBranch: snapshot.targetBranch,
          baseSha: snapshot.baseSha,
          candidateHeadSha: snapshot.candidateHeadSha,
          mergeBaseSha: snapshot.mergeBaseSha,
          canMerge: snapshot.canMerge,
          conflictFiles: snapshot.conflictFiles,
          checkedAt: snapshot.checkedAt,
          error: snapshot.error ?? 'Squash merge preflight failed',
          refreshed: true,
        },
        startedAt: new Date().toISOString(),
        completedAt: new Date().toISOString(),
        duration: 0,
      };
      steps.push(result);
      ctx.emit('integration_preflight_refreshed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        status: 'failed',
        snapshot: snapshot,
      });
      return { valid: false, steps };
    }

    const result: IntegrateStepResult = {
      step: 'integrate:preflight',
      status: 'completed',
      output: {
        step: 'integrate:preflight' as const,
        kind: snapshot.kind,
        strategy: snapshot.strategy,
        targetBranch: snapshot.targetBranch,
        baseSha: snapshot.baseSha,
        candidateHeadSha: snapshot.candidateHeadSha,
        mergeBaseSha: snapshot.mergeBaseSha,
        canMerge: snapshot.canMerge,
        conflictFiles: snapshot.conflictFiles,
        checkedAt: snapshot.checkedAt,
        refreshed: true,
      },
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      duration: 0,
    };
    steps.push(result);
    ctx.emit('integration_preflight_refreshed', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      status: 'passed',
      snapshot: snapshot,
    });

    return { valid: true, snapshot };
  }

  private failedPreflightResult(
    failingStep: string,
    targetBranch: string,
    baseSha: string | null,
    candidateHeadSha: string | null,
    mergeBaseSha: string | null,
    error: string,
  ): IntegrateStepResult {
    return {
      step: 'integrate:preflight',
      status: 'failed',
      output: {
        step: 'integrate:preflight' as const,
        failingStep,
        targetBranch,
        baseSha,
        candidateHeadSha,
        mergeBaseSha,
        error,
      },
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      duration: 0,
    };
  }

  private stepToTaskResult(step: IntegrateStepResult): StageTaskResult {
    return {
      taskId: step.step,
      title: this.taskTitle(step.step),
      status: step.status,
      artifacts: this.taskArtifacts(step),
      output: step.output,
      attempts: 1,
      duration: step.duration,
      reason: step.status === 'failed' ? this.failureReason(step.output) : undefined,
    };
  }

  private taskTitle(taskId: IntegrateTaskId): string {
    if (taskId === 'integrate:spec-sync') return 'Sync OpenSpec delta specs to main specs';
    if (taskId === 'integrate:archive-change') return 'Archive OpenSpec change';
    return 'Merge approved candidate to target branch';
  }

  private taskArtifacts(step: IntegrateStepResult): string[] {
    if (step.step !== 'integrate:archive-change') return [];
    const output = step.output as { archivePath?: unknown } | undefined;
    return typeof output?.archivePath === 'string' ? [output.archivePath] : [];
  }

  private failureReason(output: unknown): string | undefined {
    if (!output || typeof output !== 'object') return undefined;
    const error = (output as { error?: unknown }).error;
    return typeof error === 'string' ? error : undefined;
  }

  private async runSpecSyncStep(
    ctx: StageContext,
    steps: IntegrateStepResult[],
    opts: { effectiveChangeDir: string; isAlreadyArchived: boolean; projectPath: string },
    reportTask = true,
  ): Promise<IntegrateStepResult | undefined> {
    const startedAt = new Date().toISOString();

    if (opts.isAlreadyArchived) {
      const duration = Date.now() - new Date(startedAt).getTime();
      const result: IntegrateStepResult = {
        step: 'integrate:spec-sync',
        status: 'completed',
        output: {
          step: 'integrate:spec-sync' as const,
          skipped: true,
          reason: 'change-already-archived',
          archivePath: path.relative(opts.projectPath, opts.effectiveChangeDir),
        },
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(result);
      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));
      log.info('Spec sync already applied; continuing integration', {
        issueNumber: ctx.issue.number,
        archivedChangeDir: opts.effectiveChangeDir,
      });
      return result;
    }

    try {
      log.info('Running spec sync', { issueNumber: ctx.issue.number, changeDir: opts.effectiveChangeDir, projectPath: opts.projectPath });

      const summary = await this.integrator.apply(opts.effectiveChangeDir, opts.projectPath);

      const stepOutput = {
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
        corrections: summary.corrections,
        valid: summary.valid,
        errors: summary.errors,
        mode: summary.mode,
      };

      const duration = Date.now() - new Date(startedAt).getTime();
      const result: IntegrateStepResult = {
        step: 'integrate:spec-sync',
        status: summary.valid ? 'completed' : 'failed',
        output: stepOutput,
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };

      steps.push(result);

      ctx.emit('integration_step_updated', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        step: 'integrate:spec-sync',
        status: result.status,
        summary: summary.valid
          ? `Spec sync completed: ${summary.capabilities.length} capabilities, ${summary.added} added, ${summary.modified} modified`
          : `Spec sync failed: ${summary.errors.join('; ')}`,
        output: stepOutput,
      });

      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      if (!summary.valid) {
        const errMsg = `Spec sync failed: ${summary.errors.join('; ')}`;
        log.warn('Spec sync failed', { issueNumber: ctx.issue.number, errors: summary.errors });
        throw new Error(errMsg);
      }

      log.info('Spec sync succeeded', { issueNumber: ctx.issue.number, capabilities: summary.capabilities });
      return result;
    } catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(startedAt).getTime();
      const previousResult = steps.find(s => s.step === 'integrate:spec-sync');

      const result: IntegrateStepResult = {
        step: 'integrate:spec-sync',
        status: 'failed',
        output: previousResult?.output ?? { error: err instanceof Error ? err.message : String(err) },
        startedAt,
        completedAt,
        duration,
      };
      if (!steps.some(s => s.step === 'integrate:spec-sync')) {
        steps.push(result);
      }

      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      ctx.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'integrate:spec-sync',
        error: err instanceof Error ? err.message : String(err),
        output: result.output,
      });

      log.warn('Integrate stage failed at spec-sync', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }
  }

  private async runArchiveStep(
    ctx: StageContext,
    steps: IntegrateStepResult[],
    opts: { effectiveChangeDir: string; isAlreadyArchived: boolean; expectedArchivePath: string; projectPath: string },
    reportTask = true,
  ): Promise<IntegrateStepResult | undefined> {
    const startedAt = new Date().toISOString();

    if (opts.isAlreadyArchived) {
      const duration = Date.now() - new Date(startedAt).getTime();
      const archivePath = path.relative(opts.projectPath, opts.effectiveChangeDir);
      const result: IntegrateStepResult = {
        step: 'integrate:archive-change',
        status: 'completed',
        output: {
          step: 'integrate:archive-change' as const,
          archivePath,
          success: true,
          skipped: true,
          reason: 'change-already-archived',
        },
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(result);
      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));
      log.info('Archive already applied; continuing integration', {
        issueNumber: ctx.issue.number,
        archivePath,
      });
      return result;
    }

    try {
      log.info('Archiving OpenSpec change', { issueNumber: ctx.issue.number, changeDir: opts.effectiveChangeDir });

      await ctx.artifactManager.archiveChange(ctx.issue.number);

      const duration = Date.now() - new Date(startedAt).getTime();
      const result: IntegrateStepResult = {
        step: 'integrate:archive-change',
        status: 'completed',
        output: {
          step: 'integrate:archive-change' as const,
          archivePath: opts.expectedArchivePath,
          success: true,
        },
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };

      steps.push(result);

      ctx.emit('integration_step_updated', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        step: 'integrate:archive-change',
        status: 'completed',
        summary: `Archive completed: ${opts.expectedArchivePath}`,
        output: result.output,
      });

      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      log.info('Archive succeeded', { issueNumber: ctx.issue.number });
      return result;
    } catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(startedAt).getTime();

      const result: IntegrateStepResult = {
        step: 'integrate:archive-change',
        status: 'failed',
        output: { error: err instanceof Error ? err.message : String(err) },
        startedAt,
        completedAt,
        duration,
      };
      steps.push(result);

      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      ctx.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'integrate:archive-change',
        error: err instanceof Error ? err.message : String(err),
        output: result.output,
      });

      log.warn('Integrate stage failed at archive-change', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }
  }

  private async runMergeStep(
    ctx: StageContext,
    steps: IntegrateStepResult[],
    reportTask = true,
  ): Promise<IntegrateStepResult | undefined> {
    const startedAt = new Date().toISOString();

    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      throw new Error(`Project not found: ${ctx.issue.projectId}`);
    }
    const baseBranch = project.baseBranch;

    if (ctx.issue.mergeState === MergeState.Merged) {
      const duration = Date.now() - new Date(startedAt).getTime();
      const result: IntegrateStepResult = {
        step: 'integrate:merge',
        status: 'completed',
        output: {
          step: 'integrate:merge' as const,
          targetBranch: baseBranch,
          skipped: true,
          reason: 'already-merged',
        },
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(result);
      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      ctx.emit('integration_step_updated', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        step: 'integrate:merge',
        status: 'completed',
        summary: 'Merge already completed; continuing to final health gate',
        output: result.output,
      });

      log.info('Integration merge already completed; continuing to final health', {
        issueNumber: ctx.issue.number,
        targetBranch: baseBranch,
      });
      return result;
    }

    log.info('Running integration merge', {
      issueNumber: ctx.issue.number,
      projectId: ctx.issue.projectId,
      baseBranch,
    });

    if (!ctx.worktreeManager.mergeApprovedCandidate) {
      throw new Error('worktreeManager.mergeApprovedCandidate is not available');
    }

    try {
      const mergeTruth = await ctx.worktreeManager.mergeApprovedCandidate(
        project.path,
        project.name,
        ctx.issue.number,
        baseBranch
      );

      if ('failingStep' in mergeTruth) {
        const duration = Date.now() - new Date(startedAt).getTime();
        const result: IntegrateStepResult = {
          step: 'integrate:merge',
          status: 'failed',
          output: {
            step: 'integrate:merge' as const,
            failingStep: mergeTruth.failingStep,
            targetBranch: mergeTruth.targetBranch,
            strategy: (mergeTruth as any).strategy ?? 'squash',
            baseSha: mergeTruth.baseSha,
            candidateHeadSha: mergeTruth.candidateHeadSha,
            mergeBaseSha: (mergeTruth as any).mergeBaseSha ?? '',
            conflictFiles: mergeTruth.conflictFiles,
            error: mergeTruth.error,
          },
          startedAt,
          completedAt: new Date().toISOString(),
          duration,
        };
        steps.push(result);
        if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

        ctx.emit('integration_failed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          failingStep: 'integrate:merge',
          error: `Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}`,
          output: result.output,
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
        (err as any).mergeStep = result;
        throw err;
      }

      const duration = Date.now() - new Date(startedAt).getTime();
      const result: IntegrateStepResult = {
        step: 'integrate:merge',
        status: 'completed',
        output: {
          step: 'integrate:merge' as const,
          targetBranch: mergeTruth.targetBranch,
          baseSha: mergeTruth.baseSha,
          candidateHeadSha: mergeTruth.candidateHeadSha,
          landedSha: mergeTruth.landedSha,
          rebased: mergeTruth.rebased,
        },
        startedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(result);
      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      ctx.emit('integration_step_updated', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        step: 'integrate:merge',
        status: 'completed',
        summary: `Squash merge completed: ${mergeTruth.landedSha}${mergeTruth.rebased ? ' (rebased=true)' : ''}`,
        output: result.output,
      });

      log.info('Integration merge succeeded', {
        issueNumber: ctx.issue.number,
        targetBranch: mergeTruth.targetBranch,
        baseSha: mergeTruth.baseSha,
        candidateHeadSha: mergeTruth.candidateHeadSha,
        landedSha: mergeTruth.landedSha,
        rebased: mergeTruth.rebased,
      });

      if (ctx.issueRepo.setMergeState) {
        ctx.issueRepo.setMergeState(ctx.issue.id, MergeState.Merged);
      }

      return result;
    } catch (err) {
      const completedAt = new Date().toISOString();
      const duration = Date.now() - new Date(startedAt).getTime();
      const previousResult = steps.find(s => s.step === 'integrate:merge');

      const result: IntegrateStepResult = {
        step: 'integrate:merge',
        status: 'failed',
        output: previousResult?.output ?? { error: err instanceof Error ? err.message : String(err) },
        startedAt,
        completedAt,
        duration,
      };
      if (!steps.some(s => s.step === 'integrate:merge')) {
        steps.push(result);
      }

      if (reportTask) this.appendTaskResult(ctx, this.stepToTaskResult(result));

      ctx.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'integrate:merge',
        error: err instanceof Error ? err.message : String(err),
        output: result.output,
      });

      log.warn('Integrate stage failed at merge', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }
  }

  protected getChecks(): Check[] {
    return [
      new HealthGateCheck({
        worktreePath: this.worktreePath,
        policy: this.integrateHealthGatePolicy,
        stage: 'integrate',
      }),
    ];
  }

  protected async executeReportedTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult | undefined,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    if (taskId === 'integrate:spec-sync' || taskId === 'integrate:archive-change' || taskId === 'integrate:merge') {
      const steps: IntegrateStepResult[] = [];
      this.emitIntegrationStarted(ctx);
      const integration = this.resolveIntegrationContext(ctx);

      if (taskId === 'integrate:spec-sync') {
        const result = await this.runSpecSyncStep(ctx, steps, {
          effectiveChangeDir: integration.effectiveChangeDir,
          isAlreadyArchived: integration.isAlreadyArchived,
          projectPath: integration.projectPath,
        }, false);
        return result ? this.stepToTaskResult(result) : null;
      }

      if (taskId === 'integrate:archive-change') {
        const result = await this.runArchiveStep(ctx, steps, {
          effectiveChangeDir: integration.effectiveChangeDir,
          isAlreadyArchived: integration.isAlreadyArchived,
          expectedArchivePath: integration.expectedArchivePath,
          projectPath: integration.projectPath,
        }, false);
        return result ? this.stepToTaskResult(result) : null;
      }

      const result = await this.runMergeStep(ctx, steps, false);
      if (result?.status === 'completed') {
        ctx.emit('integration_completed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          steps: steps.map(s => ({ step: s.step, status: s.status, output: s.output })),
        });
      }
      return result ? this.stepToTaskResult(result) : null;
    }

    if (taskId === 'rebase-branch') {
      return executeRebaseBranchTask(ctx, attempt);
    }

    if (taskId !== 'fix-integrate-health') return null;
    const adapter = createRepairFixAdapter();
    return adapter.dispatch('fix-integrate-health', ctx, {
      worktreePath: this.worktreePath,
      failedCheck: failedCheck ?? { name: 'health:integrate', status: 'fail' as const },
      attempt,
    });
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }
}
