import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage } from '../types';
import type { StageContext } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import { Log } from '../util/log';
import { OpenSpecIntegrator } from '../openspec/open-spec-integrator';
import { loadHealthGatePolicies, type HealthGatePolicy } from './workflow-loader';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'integrate-stage-runner' });

const MAX_LOG_LENGTH = 50000;

function truncateLog(text: string, maxLength: number = MAX_LOG_LENGTH): string {
  if (text.length <= maxLength) return text;
  const half = Math.floor(maxLength / 2);
  return text.slice(0, half) + '\n\n...[truncated]...\n\n' + text.slice(-half);
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

interface IntegrateStepResult {
  step: 'integrate:spec-sync' | 'integrate:archive-change' | 'integrate:merge' | 'final-health';
  status: 'completed' | 'failed';
  output: unknown;
  startedAt: string;
  completedAt: string;
  duration: number;
}

export interface HealthGateResult {
  passed: boolean;
  enabled: boolean;
  command: string;
  timeout: number;
  duration: number;
  exitCode?: number;
  timedOut: boolean;
  summary: string;
  logExcerpt: string;
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

    ctx.eventBus.emit('integration_started', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
    });

    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    const archivedChangeDir = changeDir ? null : findArchivedChangePath(this.worktreePath, ctx.issue.number);
    const isAlreadyArchived = !changeDir && Boolean(archivedChangeDir);
    const effectiveChangeDir = changeDir ?? archivedChangeDir;
    if (!effectiveChangeDir) {
      throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
    }

    const projectPath = this.worktreePath;

    const specSyncStartedAt = new Date().toISOString();
    let specSyncResult: IntegrateStepResult | undefined;

    if (isAlreadyArchived) {
      const duration = Date.now() - new Date(specSyncStartedAt).getTime();
      specSyncResult = {
        step: 'integrate:spec-sync',
        status: 'completed',
        output: {
          step: 'integrate:spec-sync' as const,
          skipped: true,
          reason: 'change-already-archived',
          archivePath: path.relative(projectPath, effectiveChangeDir),
        },
        startedAt: specSyncStartedAt,
        completedAt: new Date().toISOString(),
        duration,
      };
      steps.push(specSyncResult);
      this.appendTaskResult(ctx, {
        taskId: 'integrate:spec-sync',
        title: 'Sync OpenSpec delta specs to main specs',
        status: 'completed',
        artifacts: [path.relative(projectPath, effectiveChangeDir)],
        attempts: 1,
        duration,
      });
      log.info('Spec sync already applied; continuing integration', {
        issueNumber: ctx.issue.number,
        archivedChangeDir: effectiveChangeDir,
      });
    } else {
      try {
        log.info('Running spec sync', { issueNumber: ctx.issue.number, changeDir: effectiveChangeDir, projectPath });

        const summary = await this.integrator.apply(effectiveChangeDir, projectPath);

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
          corrections: summary.corrections,
          valid: summary.valid,
          errors: summary.errors,
          mode: summary.mode,
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

        ctx.eventBus.emit('integration_step_updated', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          step: 'integrate:spec-sync',
          status: specSyncResult.status,
          summary: summary.valid
            ? `Spec sync completed: ${summary.capabilities.length} capabilities, ${summary.added} added, ${summary.modified} modified`
            : `Spec sync failed: ${summary.errors.join('; ')}`,
          output: specSyncOutput,
        });

        this.appendTaskResult(ctx, {
          taskId: 'integrate:spec-sync',
          title: 'Sync OpenSpec delta specs to main specs',
          status: summary.valid ? 'completed' : 'failed',
          artifacts: [],
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

        ctx.eventBus.emit('integration_failed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          failingStep: 'integrate:spec-sync',
          error: err instanceof Error ? err.message : String(err),
          output: specSyncResult.output,
        });

        log.warn('Integrate stage failed at spec-sync', {
          issueNumber: ctx.issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
        throw err;
      }
    }

    const archiveStartedAt = new Date().toISOString();
    let archiveResult: IntegrateStepResult | undefined;

    const changeName = isAlreadyArchived
      ? path.basename(effectiveChangeDir).replace(/^\d{4}-\d{2}-\d{2}-/, '')
      : path.basename(effectiveChangeDir);
    const now = new Date();
    const datePrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    const expectedArchiveName = `${datePrefix}-${changeName}`;
    const expectedArchivePath = `openspec/changes/archive/${expectedArchiveName}`;

    if (isAlreadyArchived) {
      const duration = Date.now() - new Date(archiveStartedAt).getTime();
      const archivePath = path.relative(projectPath, effectiveChangeDir);
      archiveResult = {
        step: 'integrate:archive-change',
        status: 'completed',
        output: {
          step: 'integrate:archive-change' as const,
          archivePath,
          success: true,
          skipped: true,
          reason: 'change-already-archived',
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
        artifacts: [archivePath],
        attempts: 1,
        duration,
      });
      log.info('Archive already applied; continuing integration', {
        issueNumber: ctx.issue.number,
        archivePath,
      });
    } else {
      try {
        log.info('Archiving OpenSpec change', { issueNumber: ctx.issue.number, changeDir: effectiveChangeDir });

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

        ctx.eventBus.emit('integration_step_updated', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          step: 'integrate:archive-change',
          status: 'completed',
          summary: `Archive completed: ${expectedArchivePath}`,
          output: archiveResult.output,
        });

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

        ctx.eventBus.emit('integration_failed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          failingStep: 'integrate:archive-change',
          error: err instanceof Error ? err.message : String(err),
          output: archiveResult.output,
        });

        log.warn('Integrate stage failed at archive-change', {
          issueNumber: ctx.issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
        throw err;
      }
    }

    const mergeStartedAt = new Date().toISOString();
    let mergeResult: IntegrateStepResult | undefined;

    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      throw new Error(`Project not found: ${ctx.issue.projectId}`);
    }
    const baseBranch = project.baseBranch;

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
        project.path,
        project.name,
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

        ctx.eventBus.emit('integration_failed', {
          issueId: ctx.issue.id,
          projectId: ctx.issue.projectId,
          issueNumber: ctx.issue.number,
          failingStep: 'integrate:merge',
          error: `Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}`,
          output: mergeResult.output,
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
        artifacts: [],
        attempts: 1,
        duration,
        output: {
          kind: 'integrate-merge',
          targetBranch: mergeTruth.targetBranch,
          baseSha: mergeTruth.baseSha,
          candidateHeadSha: mergeTruth.candidateHeadSha,
          landedSha: mergeTruth.landedSha,
          fastForward: mergeTruth.fastForward,
          rebased: mergeTruth.rebased,
        },
      });

      ctx.eventBus.emit('integration_step_updated', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        step: 'integrate:merge',
        status: 'completed',
        summary: `Merge completed: ${mergeTruth.landedSha} (fastForward=${mergeTruth.fastForward}, rebased=${mergeTruth.rebased})`,
        output: mergeResult.output,
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

      ctx.eventBus.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'integrate:merge',
        error: err instanceof Error ? err.message : String(err),
        output: mergeResult.output,
      });

      log.warn('Integrate stage failed at merge', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      throw err;
    }

    const finalHealthResult = await this.runFinalHealthGate(ctx);

    if (!finalHealthResult.passed) {
      const failingStep: IntegrateStepResult = {
        step: 'final-health',
        status: 'failed',
        output: {
          kind: 'health-gate',
          stage: 'integrate',
          command: finalHealthResult.command,
          timeout: finalHealthResult.timeout,
          duration: finalHealthResult.duration,
          enabled: finalHealthResult.enabled,
          passed: finalHealthResult.passed,
          exitCode: finalHealthResult.exitCode,
          timedOut: finalHealthResult.timedOut,
          summary: finalHealthResult.summary,
          logExcerpt: finalHealthResult.logExcerpt,
        },
        startedAt: '',
        completedAt: new Date().toISOString(),
        duration: finalHealthResult.duration,
      };
      steps.push(failingStep);
      this.appendTaskResult(ctx, {
        taskId: 'final-health',
        title: 'Run final integration health gate',
        status: 'failed',
        artifacts: [],
        attempts: 1,
        duration: finalHealthResult.duration,
        output: {
          kind: 'health-gate',
          stage: 'integrate',
          command: finalHealthResult.command,
          passed: false,
          summary: finalHealthResult.summary,
        },
      });

      ctx.eventBus.emit('integration_failed', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        issueNumber: ctx.issue.number,
        failingStep: 'final-health',
        error: `Final health gate failed: ${finalHealthResult.summary}`,
        output: failingStep.output,
      });

      log.warn('Integrate stage failed at final-health', {
        issueNumber: ctx.issue.number,
        command: finalHealthResult.command,
        summary: finalHealthResult.summary,
      });
      throw new Error(`Final health gate failed: ${finalHealthResult.summary}`);
    }

    const healthGateStep: IntegrateStepResult = {
      step: 'final-health',
      status: 'completed',
      output: {
        kind: 'health-gate',
        stage: 'integrate',
        command: finalHealthResult.command,
        timeout: finalHealthResult.timeout,
        duration: finalHealthResult.duration,
        enabled: finalHealthResult.enabled,
        passed: finalHealthResult.passed,
        exitCode: finalHealthResult.exitCode,
        timedOut: finalHealthResult.timedOut,
        summary: finalHealthResult.summary,
        logExcerpt: finalHealthResult.logExcerpt,
      },
      startedAt: '',
      completedAt: new Date().toISOString(),
      duration: finalHealthResult.duration,
    };
    steps.push(healthGateStep);
    this.appendTaskResult(ctx, {
      taskId: 'final-health',
      title: 'Run final integration health gate',
      status: 'completed',
      artifacts: [],
      attempts: 1,
      duration: finalHealthResult.duration,
      output: {
        kind: 'health-gate',
        stage: 'integrate',
        command: finalHealthResult.command,
        passed: true,
        summary: finalHealthResult.summary,
      },
    });

    ctx.eventBus.emit('integration_step_updated', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      step: 'final-health',
      status: 'completed',
      summary: `Final health gate passed (command: ${finalHealthResult.command})`,
      output: healthGateStep.output,
    });

    log.info('Final health gate passed', {
      issueNumber: ctx.issue.number,
      command: finalHealthResult.command,
      duration: finalHealthResult.duration,
    });

    ctx.eventBus.emit('integration_completed', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      steps: steps.map(s => ({ step: s.step, status: s.status, output: s.output })),
    });

    return {
      integrate: true,
      steps,
    };
  }

  private async runFinalHealthGate(ctx: StageContext): Promise<HealthGateResult> {
    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      return {
        passed: false,
        enabled: false,
        command: '',
        timeout: 0,
        duration: 0,
        timedOut: false,
        summary: 'Project not found',
        logExcerpt: '',
      };
    }

    const { loadWorkflow } = await import('./workflow-loader');
    const workflow = loadWorkflow(project.path);
    if (typeof workflow === 'string') {
      return {
        passed: false,
        enabled: false,
        command: '',
        timeout: 0,
        duration: 0,
        timedOut: false,
        summary: 'Failed to load workflow config',
        logExcerpt: '',
      };
    }

    const policies = loadHealthGatePolicies(workflow);
    const policy = policies.postMerge;

    if (!policy.enabled) {
      log.info('Final health gate disabled, completing without verification', {
        issueNumber: ctx.issue.number,
      });
      return {
        passed: true,
        enabled: false,
        command: policy.command,
        timeout: policy.timeout,
        duration: 0,
        timedOut: false,
        summary: 'Final health gate disabled',
        logExcerpt: '',
      };
    }

    return this.executeHealthCommand(project.path, policy);
  }

  private async executeHealthCommand(projectPath: string, policy: HealthGatePolicy): Promise<HealthGateResult> {
    const startTime = Date.now();
    const { command, timeout } = policy;

    try {
      const { stdout, stderr } = await execFileAsync(command, [], {
        cwd: projectPath,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });

      const duration = Date.now() - startTime;

      return {
        passed: true,
        enabled: true,
        command,
        timeout,
        duration,
        timedOut: false,
        summary: 'Final health gate passed',
        logExcerpt: truncateLog(stdout + '\n' + stderr, 5000),
      };
    } catch (err: any) {
      const duration = Date.now() - startTime;
      const isTimeout = err.killed === true;
      const stderr = err.stderr || '';
      const stdout = err.stdout || '';

      let exitCode = err.code;
      if (typeof exitCode !== 'number' && err.message) {
        const match = err.message.match(/exit code (\d+)/);
        if (match) exitCode = parseInt(match[1], 10);
      }

      const summary = this.formatErrorMessage(command, stderr, stdout, exitCode, isTimeout);

      return {
        passed: false,
        enabled: true,
        command,
        timeout,
        duration,
        exitCode: typeof exitCode === 'number' ? exitCode : undefined,
        timedOut: isTimeout,
        summary,
        logExcerpt: truncateLog([stdout, stderr, err.message].filter(Boolean).join('\n'), 5000),
      };
    }
  }

  private formatErrorMessage(
    command: string,
    stderr: string,
    stdout: string,
    exitCode: number | undefined,
    isTimeout: boolean,
  ): string {
    if (isTimeout) {
      return `${command} — 超时`;
    }

    const combined = [stdout, stderr].filter(Boolean).join('\n');
    const lines = combined.split('\n');
    const errorLines: string[] = [];
    const errorPatterns = [/error/i, /fail/i, /cannot find/i, /not found/i, /unexpected/i, /syntax error/i];

    for (const line of lines) {
      if (errorPatterns.some(p => p.test(line))) {
        errorLines.push(line);
      }
      if (errorLines.length >= 15) break;
    }

    if (errorLines.length === 0) {
      const tail = lines.filter(l => l.trim()).slice(-15);
      errorLines.push(...tail);
    }

    const keyErrors = errorLines.join('\n');
    const parts: string[] = [];

    if (typeof exitCode === 'number') {
      parts.push(`${command} 失败 (exit code ${exitCode})`);
    } else {
      parts.push(`${command} 失败`);
    }

    if (keyErrors) {
      const oneLine = keyErrors.split('\n').filter(l => l.trim()).slice(0, 3).join(' | ');
      if (oneLine.length > 200) {
        parts.push(oneLine.slice(0, 200) + '...');
      } else {
        parts.push(oneLine);
      }
    }

    return parts.join(' — ');
  }

  protected getChecks(): import('./checks').Check[] {
    return [];
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }
}
