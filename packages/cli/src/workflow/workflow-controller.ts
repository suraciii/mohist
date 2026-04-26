import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage, IssueStatus, MergeState, type Issue } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { loadWorkflow } from './workflow-loader';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt, buildReviewSelfCheckPrompt, buildConflictResolutionPrompt, type ArtifactType } from '../agents/artifact-prompt';
import type { IssueRepo } from '../db/issue-repo';
import type { EventBus } from '../services/event-bus';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { Log } from '../util/log';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'workflow' });

export interface ChangeArtifactsManager {
  getChangeDir(issueNumber: number): string | null;
  createChangeDir(issueNumber: number, title: string): string | null;
  readArtifact(changeDir: string, artifactPath: string): string | null;
  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean;
  exists(changeDir: string): boolean;
  readTasks(issueNumber: number): TasksFile | null;
  updateTaskPasses(issueNumber: number, taskId: string, passes: boolean, error?: string | null): boolean;
}

export interface StageResult {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
}

export interface WorkflowControllerOptions {
  artifactManager: ChangeArtifactsManager;
  worktreePath: string;
  issueRepo?: IssueRepo;
  eventBus?: EventBus;
  projectId?: string;
  checkpointRepo?: PipelineCheckpointRepo;
}

export interface PipelineResult {
  completed: boolean;
  stage: Stage;
  gateRequired: boolean;
  message?: string;
}

export class WorkflowController {
  private artifactManager: ChangeArtifactsManager;
  private worktreePath: string;
  private issueRepo?: IssueRepo;
  private eventBus?: EventBus;
  private projectId?: string;
  private checkpointRepo?: PipelineCheckpointRepo;

  constructor(options: WorkflowControllerOptions) {
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.projectId = options.projectId;
    this.checkpointRepo = options.checkpointRepo;
  }

  getCheckpointRepo(): PipelineCheckpointRepo | undefined {
    return this.checkpointRepo;
  }

  async runPlanStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const changeDir = this.artifactManager.getChangeDir(issue.number)
      || this.artifactManager.createChangeDir(issue.number, issue.title);
    if (!changeDir) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Failed to get or create change directory for issue #${issue.number}`,
      };
    }

    const checkpoint = this.checkpointRepo?.get(issue.number, 'plan') ?? null;
    const completedSteps: string[] = checkpoint ? [...checkpoint.completedSteps] : [];
    const isResuming = completedSteps.length > 0;

    if (!isResuming) {
      cleanChangeDir(changeDir);
    } else {
      log.info('Plan stage resuming from checkpoint', {
        issueNumber: issue.number,
        completedSteps,
        nextStep: checkpoint?.nextStep,
      });
    }

    const rounds: PlanRoundConfig[] = [
      { type: 'proposal', verify: () => fs.existsSync(path.join(changeDir, 'proposal.md')), label: 'proposal.md', outputPath: path.join(changeDir, 'proposal.md') },
      { type: 'specs', verify: () => fs.existsSync(path.join(changeDir, 'specs')), label: 'specs/', outputPath: path.join(changeDir, 'specs') },
      { type: 'design', verify: () => fs.existsSync(path.join(changeDir, 'design.md')), label: 'design.md', outputPath: path.join(changeDir, 'design.md') },
      { type: 'tasks', verify: () => fs.existsSync(path.join(changeDir, 'tasks.json')), label: 'tasks.json', outputPath: path.join(changeDir, 'tasks.json') },
    ];

    const roundState = { type: '', index: 0 };
    const planAcpOptions: AcpConnectionOptions = {
      ...acpOptions,
      executionId: `plan-${issue.number}`,
      onSessionUpdate: (_notification) => {
        if (!this.eventBus) return;
        try {
          this.eventBus.emit('plan_session_update', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: roundState.type,
            roundIndex: roundState.index,
            sessionUpdate: _notification.update.sessionUpdate,
            data: _notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', { error: e instanceof Error ? e.message : String(e) });
        }
      },
    };

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(planAcpOptions);

      for (const [index, round] of rounds.entries()) {
        roundState.type = round.type;
        roundState.index = index;

        if (completedSteps.includes(round.type)) {
          if (round.verify()) {
            log.info('Plan stage round skipped (checkpoint + artifact exists)', { artifact: round.type, issueNumber: issue.number });
            continue;
          }
          log.info('Plan stage round in checkpoint but artifact missing, re-running', { artifact: round.type, issueNumber: issue.number });
          const idx = completedSteps.indexOf(round.type);
          completedSteps.splice(idx);
        } else if (!completedSteps.includes(round.type) && round.verify()) {
          log.info('Plan stage artifact exists but not in checkpoint, marking complete', { artifact: round.type, issueNumber: issue.number });
          completedSteps.push(round.type);
          const nextRound = rounds[index + 1];
          this.checkpointRepo?.upsert(issue.number, 'plan', [...completedSteps], nextRound?.type ?? 'self-review');
          continue;
        }

        log.info('Plan stage round', { artifact: round.type, issueNumber: issue.number });

        if (this.eventBus) {
          try {
            this.eventBus.emit('plan_round_start', {
              issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
              projectId: this.projectId ?? issue.projectId,
              roundType: round.type,
              roundLabel: round.label,
              roundIndex: index,
            });
          } catch (e) {
            log.warn('eventBus.emit failed for plan_round_start', { error: e instanceof Error ? e.message : String(e) });
          }
        }

        const prompt = buildArtifactPrompt(round.type as ArtifactType, issue, changeDir);
        const result = await conn.prompt(prompt);

        if (!result.success) {
          log.error('Plan stage round failed', { artifact: round.type, error: result.error });
          await conn.close();
          return {
            success: false,
            requiresApproval: false,
            output: null,
            message: `Plan stage failed at artifact "${round.label}": ${result.error ?? 'unknown error'}`,
          };
        }

        if (!round.verify()) {
          log.warn('Plan stage artifact not found after round, sending retry', { artifact: round.label, roundIndex: index });

          const retryPrompt = [
            `The artifact file ${round.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${round.type} artifact to:`,
            round.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('Plan stage retry prompt sent', { artifact: round.type, roundIndex: index });

          const retryResult = await conn.prompt(retryPrompt);

          if (!retryResult.success) {
            log.error('Plan stage retry prompt failed', { artifact: round.type, error: retryResult.error });
            await conn.close();
            return {
              success: false,
              requiresApproval: false,
              output: null,
              message: `Plan stage failed: retry for artifact "${round.label}" returned error: ${retryResult.error ?? 'unknown error'}`,
            };
          }

          if (!round.verify()) {
            log.error('Plan stage artifact still missing after retry', { artifact: round.label });
            await conn.close();
            return {
              success: false,
              requiresApproval: false,
              output: null,
              message: `Plan stage failed: artifact "${round.label}" not found after retry`,
            };
          }

          log.info('Plan stage retry succeeded', { artifact: round.label });
        }

        completedSteps.push(round.type);
        const nextRound = rounds[index + 1];
        this.checkpointRepo?.upsert(issue.number, 'plan', [...completedSteps], nextRound?.type ?? 'self-review');
      }

      // self-review round
      roundState.type = 'self-review';
      roundState.index = rounds.length;

      log.info('Plan stage self-review round', { issueNumber: issue.number });

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'self-review',
            roundLabel: 'self-review',
            roundIndex: rounds.length,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 'self-review', error: e instanceof Error ? e.message : String(e) });
        }
      }

      const selfReviewPrompt = buildSelfReviewPrompt(issue, changeDir);
      const selfReviewResult = await conn.prompt(selfReviewPrompt);

      if (!selfReviewResult.success) {
        log.error('Plan stage self-review failed', { error: selfReviewResult.error });
        await conn.close();
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Plan stage failed at self-review: ${selfReviewResult.error ?? 'unknown error'}`,
        };
      }

      await conn.close();

      this.checkpointRepo?.delete(issue.number, 'plan');

      const selfReviewReport = readReportFile(changeDir, 'self-review.md') ?? selfReviewResult.text;

      return {
        success: true,
        requiresApproval: true,
        output: {
          stage: Stage.Plan,
          issueNumber: issue.number,
          selfReviewNotes: selfReviewReport,
        },
        message: 'Plan completed, awaiting user approval',
      };
    } catch (err) {
      if (conn) {
        try {
          await conn.close();
        } catch {
          // ignore cleanup errors
        }
      }
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Plan stage error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }

  async run(issue: Issue, acpOptions: AcpConnectionOptions): Promise<PipelineResult> {
    if (!this.issueRepo || !this.eventBus) {
      return {
        completed: false,
        stage: issue.stage,
        gateRequired: false,
        message: 'Pipeline requires issueRepo and eventBus',
      };
    }

    let currentIssue = issue;

    while (currentIssue.stage !== Stage.Done) {
      switch (currentIssue.stage) {
        case Stage.Draft:
        case Stage.Plan: {
          const planResult = await this.runPlanStage(currentIssue, acpOptions);
          if (!planResult.success) {
            return {
              completed: false,
              stage: Stage.Plan,
              gateRequired: false,
              message: planResult.message,
            };
          }

          this.issueRepo.updateStage(currentIssue.id, Stage.Plan);
          this.issueRepo.setApprovalState(currentIssue.id, {
            stage: Stage.Plan,
            status: 'awaiting',
            output: planResult.output,
            requestedAt: new Date().toISOString(),
          });
          this.eventBus.emit('approval_requested', {
            issueId: currentIssue.id,
            projectId: this.projectId ?? currentIssue.projectId,
            stage: Stage.Plan,
          });

          return {
            completed: false,
            stage: Stage.Plan,
            gateRequired: true,
            message: 'Plan completed, awaiting approval',
          };
        }

        case Stage.Build: {
          const buildResult = await this.runPipelineBuildStage(currentIssue, acpOptions);
          if (!buildResult.success) {
            return {
              completed: false,
              stage: Stage.Build,
              gateRequired: false,
              message: buildResult.message,
            };
          }

          currentIssue = this.issueRepo.updateStage(currentIssue.id, Stage.Review)!;
          break;
        }

        case Stage.Review: {
          const reviewResult = await this.runPipelineReviewStage(currentIssue, acpOptions);
          if (!reviewResult.success) {
            return {
              completed: false,
              stage: Stage.Review,
              gateRequired: false,
              message: reviewResult.message,
            };
          }

          if (currentIssue.mergeState === MergeState.Resolving) {
            log.info('Skipping approval gate during conflict resolution', { issueNumber: currentIssue.number });
            currentIssue = this.issueRepo.updateStage(currentIssue.id, Stage.Done)!;
            break;
          }

          this.issueRepo.setApprovalState(currentIssue.id, {
            stage: Stage.Review,
            status: 'awaiting',
            output: reviewResult.output,
            requestedAt: new Date().toISOString(),
          });
          this.eventBus.emit('approval_requested', {
            issueId: currentIssue.id,
            projectId: this.projectId ?? currentIssue.projectId,
            stage: Stage.Review,
          });

          return {
            completed: false,
            stage: Stage.Review,
            gateRequired: true,
            message: 'Review completed, awaiting approval',
          };
        }

        default:
          return {
            completed: false,
            stage: currentIssue.stage,
            gateRequired: false,
            message: `Pipeline cannot handle stage: ${currentIssue.stage}`,
          };
      }
    }

    this.issueRepo.updateStage(currentIssue.id, Stage.Done);
    this.issueRepo.clearApprovalState(currentIssue.id);
    this.issueRepo.updateStatus(currentIssue.id, IssueStatus.Completed);
    this.checkpointRepo?.deleteAll(currentIssue.number);

    log.info('Pipeline completed', { issueNumber: currentIssue.number });
    return { completed: true, stage: Stage.Done, gateRequired: false, message: 'Pipeline completed' };
  }

  private async commitBuildChanges(issue: Issue): Promise<void> {
    try {
      const { stdout: statusOut } = await execFileAsync(
        'git',
        ['status', '--porcelain', '--ignore-submodules'],
        { cwd: this.worktreePath },
      );

      const lines = statusOut
        .split('\n')
        .filter(l => l.trim() !== '')
        .filter(l => !l.endsWith('openspec/changes/') && !l.includes('openspec/changes/'));

      if (lines.length === 0) {
        log.info('No changes to commit after build stage', { issueNumber: issue.number });
        return;
      }

      await execFileAsync(
        'git',
        ['add', '--', ':!openspec/changes/', ':!.opencode/'],
        { cwd: this.worktreePath },
      );

      const message = `build(issue-${issue.number}): ${issue.title}`;
      await execFileAsync('git', ['commit', '-m', message, '--no-verify'], {
        cwd: this.worktreePath,
      });

      log.info('Build stage changes committed', { issueNumber: issue.number, files: lines.length });
    } catch (err) {
      log.warn('Failed to commit build stage changes', {
        issueNumber: issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private emitSafe(event: Parameters<EventBus['emit']>[0], data: Parameters<EventBus['emit']>[1]): void {
    if (!this.eventBus) return;
    try {
      this.eventBus.emit(event as keyof import('../services/event-bus').EventMap, data as never);
    } catch (e) {
      log.warn('eventBus.emit failed', { event: String(event), error: e instanceof Error ? e.message : String(e) });
    }
  }

  private getBuildStageTimeoutMs(): number | undefined {
    const config = loadWorkflow(this.worktreePath);
    if (typeof config === 'string') return undefined;
    const buildStage = config.stages.find(s => s.stage === 'build');
    if (!buildStage?.timeout) return undefined;
    return buildStage.timeout * 1000;
  }

  private writeLog(workflowLogRepo: WorkflowLogRepo | undefined, issueId: string, eventType: string, data: object): void {
    if (!workflowLogRepo) return;
    try {
      workflowLogRepo.insert(issueId, null, eventType, data);
    } catch (e) {
      log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
    }
  }

  private async runPipelineBuildStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    if (issue.mergeState === MergeState.Resolving) {
      return this.runConflictResolutionStage(issue, acpOptions);
    }

    const buildStartTime = Date.now();
    const issueId = issue.id;
    const projectId = this.projectId ?? issue.projectId;
    const workflowLogRepo = acpOptions.workflowLogRepo;

    const checkpoint = this.checkpointRepo?.get(issue.number, 'build') ?? null;
    const completedTaskIds: string[] = checkpoint ? [...checkpoint.completedSteps] : [];

    if (completedTaskIds.length > 0) {
      log.info('Build stage resuming from checkpoint', {
        issueNumber: issue.number,
        completedTaskIds,
        nextStep: checkpoint?.nextStep,
      });
    }

    const change = detectOpenSpecChange(this.worktreePath, issue);

    if (!change) {
      log.warn('detectOpenSpecChange returned null', {
        worktreePath: this.worktreePath,
        issueNumber: issue.number,
      });

      this.emitSafe('build_stage_failed', {
        issueId,
        projectId,
        reason: 'no_change_found',
        details: { worktreePath: this.worktreePath },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
        reason: 'no_change_found',
        worktreePath: this.worktreePath,
        issueNumber: issue.number,
      });

      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `No OpenSpec change found for issue #${issue.number}`,
      };
    }

    log.info('detectOpenSpecChange found change', {
      changePath: change.changePath,
      tasksPath: change.tasksPath,
      issueNumber: issue.number,
    });

    let total = 0;
    let pending = 0;
    let passed = 0;

    try {
      const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
      const tasksFile = JSON.parse(tasksContent) as import('../artifacts/change-artifacts-manager').TasksFile;
      const tasks = tasksFile.tasks;
      total = tasks.length;
      pending = tasks.filter(t => !t.passes).length;
      passed = tasks.filter(t => t.passes).length;
    } catch {
      log.warn('Failed to read tasks snapshot for build stage logging', {
        tasksPath: change.tasksPath,
        issueNumber: issue.number,
      });
    }

    log.info('Build stage tasks snapshot', {
      issueNumber: issue.number,
      total,
      pending,
      passed,
    });

    this.emitSafe('build_stage_started', {
      issueId,
      projectId,
      stage: 'build' as const,
      changePath: change.changePath,
      tasksCount: total,
      timestamp: new Date().toISOString(),
    });
    this.emitSafe('build_tasks_snapshot', {
      issueId,
      projectId,
      total,
      pending,
      passed,
    });
    this.writeLog(workflowLogRepo, issueId, 'build_started', {
      changePath: change.changePath,
      tasksCount: total,
      pending,
      passed,
    });

    const executor = new RalphExecutor({
      worktreePath: this.worktreePath,
      projectPath: this.worktreePath,
      issueId: issue.id,
      projectId: issue.projectId,
      eventBus: this.eventBus,
      executionId: `build-${issue.number}`,
      workflowLogRepo: acpOptions.workflowLogRepo,
      coderSessionRepo: acpOptions.coderSessionRepo,
      issueNumber: issue.number,
      stageTimeoutMs: this.getBuildStageTimeoutMs(),
    });

    const activeCompletedTaskIds = [...completedTaskIds];

    const result: RalphLoopResult = await executor.execute(change, {
      skipTaskIds: completedTaskIds.length > 0 ? completedTaskIds : undefined,
      onTaskCompleted: (taskId: string) => {
        if (!this.checkpointRepo) return;
        activeCompletedTaskIds.push(taskId);
        this.checkpointRepo.upsert(issue.number, 'build', [...activeCompletedTaskIds], null);
      },
    });
    const duration = Date.now() - buildStartTime;

    log.info('Ralph loop completed', {
      issueNumber: issue.number,
      completed: result.completed,
      failed: result.failed,
      total: result.total,
      success: result.success,
      duration,
    });

    if (result.completed === 0 && result.total > 0) {
      log.warn('Build completed with 0 tasks executed out of total', {
        total: result.total,
        issueNumber: issue.number,
      });

      this.emitSafe('build_stage_failed', {
        issueId,
        projectId,
        reason: 'zero_work',
        details: { completed: result.completed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
        reason: 'zero_work',
        completed: result.completed,
        total: result.total,
        duration,
      });

      return {
        success: false,
        requiresApproval: false,
        output: {
          stage: Stage.Build,
          issueNumber: issue.number,
          completedTasks: result.completed,
          failedTasks: result.failed,
          totalTasks: result.total,
        },
        message: `Build completed with 0 tasks executed out of ${result.total} total — tasks may have been pre-marked as passed`,
      };
    }

    if (result.success) {
      await this.commitBuildChanges(issue);

      this.checkpointRepo?.delete(issue.number, 'build');

      this.emitSafe('build_stage_completed', {
        issueId,
        projectId,
        completed: result.completed,
        failed: result.failed,
        total: result.total,
        duration,
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_completed', {
        completed: result.completed,
        failed: result.failed,
        total: result.total,
        duration,
      });
    } else {
      this.emitSafe('build_stage_failed', {
        issueId,
        projectId,
        reason: 'tasks_failed',
        details: { completed: result.completed, failed: result.failed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
        reason: 'tasks_failed',
        completed: result.completed,
        failed: result.failed,
        total: result.total,
        duration,
      });
    }

    return {
      success: result.success,
      requiresApproval: false,
      output: {
        stage: Stage.Build,
        issueNumber: issue.number,
        completedTasks: result.completed,
        failedTasks: result.failed,
        totalTasks: result.total,
      },
      message: result.success
        ? `Build completed - ${result.completed}/${result.total} tasks executed`
        : `Build completed with ${result.failed} failed task(s)`,
    };
  }

  private async runConflictResolutionStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const buildStartTime = Date.now();
    const issueId = issue.id;
    const projectId = this.projectId ?? issue.projectId;
    const workflowLogRepo = acpOptions.workflowLogRepo;

    this.emitSafe('build_stage_started', {
      issueId,
      projectId,
      stage: 'build' as const,
      changePath: this.worktreePath,
      tasksCount: 0,
      timestamp: new Date().toISOString(),
    });
    this.writeLog(workflowLogRepo, issueId, 'build_started', {
      reason: 'conflict_resolution',
      worktreePath: this.worktreePath,
      issueNumber: issue.number,
    });

    let conflictFiles: string[] = [];
    try {
      const { stdout } = await execFileAsync('git', ['diff', '--name-only', '--diff-filter=U'], { cwd: this.worktreePath });
      conflictFiles = stdout.split('\n').filter(f => f.trim() !== '');
    } catch (err) {
      log.warn('Failed to detect conflict files', { error: err instanceof Error ? err.message : String(err) });
    }

    log.info('Conflict resolution: detected conflict files', {
      issueNumber: issue.number,
      conflictFiles: conflictFiles.length,
    });

    if (conflictFiles.length === 0) {
      log.info('No conflict files found, committing any pending changes', { issueNumber: issue.number });
      await this.commitBuildChanges(issue);

      if (this.issueRepo) {
        this.issueRepo.updateMergeState(issue.id, MergeState.Pending);
      }

      return {
        success: true,
        requiresApproval: false,
        output: { stage: Stage.Build, issueNumber: issue.number, conflictFiles: 0 },
        message: 'No conflict files found, proceeding',
      };
    }

    const changeDir = this.artifactManager.getChangeDir(issue.number) ?? this.worktreePath;
    const prompt = buildConflictResolutionPrompt(issue, changeDir, conflictFiles);

    const resolveAcpOptions: AcpConnectionOptions = {
      ...acpOptions,
      executionId: `conflict-resolve-${issue.number}`,
      onSessionUpdate: (notification) => {
        if (!this.eventBus) return;
        try {
          this.eventBus.emit('plan_session_update', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId,
            roundType: 'conflict-resolution',
            roundIndex: 0,
            sessionUpdate: notification.update.sessionUpdate,
            data: notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for conflict resolution session update', {
            error: e instanceof Error ? e.message : String(e),
          });
        }
      },
    };

    let conn: AcpConnection | undefined;
    try {
      conn = await createAcpConnection(resolveAcpOptions);
      const result = await conn.prompt(prompt);
      await conn.close();

      const duration = Date.now() - buildStartTime;

      if (!result.success) {
        log.error('Conflict resolution agent session failed', {
          issueNumber: issue.number,
          error: result.error,
          duration,
        });

        this.emitSafe('build_stage_failed', {
          issueId,
          projectId,
          reason: 'conflict_resolution_failed',
          details: { error: result.error, conflictFiles: conflictFiles.length },
          timestamp: new Date().toISOString(),
        });
        this.writeLog(workflowLogRepo, issueId, 'build_failed', {
          reason: 'conflict_resolution_failed',
          error: result.error,
          duration,
        });

        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Conflict resolution failed: ${result.error ?? 'unknown error'}`,
        };
      }

      await this.commitBuildChanges(issue);

      if (this.issueRepo) {
        this.issueRepo.updateMergeState(issue.id, MergeState.Pending);
      }

      log.info('Conflict resolution completed', {
        issueNumber: issue.number,
        conflictFiles: conflictFiles.length,
        duration,
      });

      this.emitSafe('build_stage_completed', {
        issueId,
        projectId,
        completed: 0,
        failed: 0,
        total: 0,
        duration,
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_completed', {
        reason: 'conflict_resolution',
        conflictFiles: conflictFiles.length,
        duration,
      });

      return {
        success: true,
        requiresApproval: false,
        output: {
          stage: Stage.Build,
          issueNumber: issue.number,
          conflictFiles: conflictFiles.length,
        },
        message: `Conflict resolution completed - ${conflictFiles.length} conflict(s) resolved`,
      };
    } catch (err) {
      if (conn) {
        try { await conn.close(); } catch { /* ignore */ }
      }
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Conflict resolution error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }

  private async runPipelineReviewStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const changeDir = this.artifactManager.getChangeDir(issue.number);
    if (!changeDir) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Change directory not found for issue #${issue.number}`,
      };
    }

    const roundState = { type: '', index: 0 };

    const reviewAcpOptions: AcpConnectionOptions = {
      ...acpOptions,
      executionId: `review-${issue.number}`,
      onSessionUpdate: (_notification) => {
        if (!this.eventBus) return;
        try {
          this.eventBus.emit('plan_session_update', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: roundState.type,
            roundIndex: roundState.index,
            sessionUpdate: _notification.update.sessionUpdate,
            data: _notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', { roundType: roundState.type, error: e instanceof Error ? e.message : String(e) });
        }
      },
    };

    let conn: AcpConnection | undefined;
    try {
      conn = await createAcpConnection(reviewAcpOptions);

      // Round 0: review
      roundState.type = 'review';
      roundState.index = 0;

      log.info('Review stage round', { roundType: 'review', issueNumber: issue.number });

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'review',
            roundLabel: 'review',
            roundIndex: 0,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 'review', error: e instanceof Error ? e.message : String(e) });
        }
      }

      const reviewerPrompt = buildReviewerPrompt(issue, changeDir);
      const result = await conn.prompt(reviewerPrompt);

      if (!result.success) {
        log.error('Review stage round failed', { roundType: 'review', error: result.error });
        await conn.close();
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Review failed: ${result.error ?? 'unknown error'}`,
        };
      }

      // Round 1: self-check
      roundState.type = 'review-self-check';
      roundState.index = 1;

      log.info('Review stage self-check round', { issueNumber: issue.number });

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'review-self-check',
            roundLabel: 'review-self-check',
            roundIndex: 1,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 'review-self-check', error: e instanceof Error ? e.message : String(e) });
        }
      }

      const selfCheckPrompt = buildReviewSelfCheckPrompt(issue, changeDir);
      const selfCheckResult = await conn.prompt(selfCheckPrompt);

      if (!selfCheckResult.success) {
        log.error('Review stage self-check failed', { error: selfCheckResult.error });
        await conn.close();
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Review stage failed at self-check: ${selfCheckResult.error ?? 'unknown error'}`,
        };
      }

      await conn.close();

      const reviewReport = readReportFile(changeDir, 'review.md') ?? selfCheckResult.text;

      if (!reviewReport || reviewReport.trim().length === 0) {
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: 'Review stage failed: review.md is empty after self-check',
        };
      }

      return {
        success: true,
        requiresApproval: true,
        output: {
          stage: Stage.Review,
          issueNumber: issue.number,
          reviewReport,
        },
        message: 'Review completed, awaiting user approval',
      };
    } catch (err) {
      if (conn) {
        try {
          await conn.close();
        } catch {
          // ignore cleanup errors
        }
      }
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Review stage error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}

interface PlanRoundConfig {
  type: string;
  verify: () => boolean;
  label: string;
  outputPath: string;
}

function readReportFile(changeDir: string, filename: string): string | null {
  const filePath = path.join(changeDir, filename);
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8').trim();
    return content.length > 0 ? content : null;
  } catch {
    return null;
  }
}

function cleanChangeDir(changeDir: string): void {
  if (!fs.existsSync(changeDir)) {
    return;
  }

  const entries = fs.readdirSync(changeDir);
  for (const entry of entries) {
    if (entry === '.openspec.yaml') continue;
    const entryPath = path.join(changeDir, entry);
    fs.rmSync(entryPath, { recursive: true, force: true });
  }
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}
