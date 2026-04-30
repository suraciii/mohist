import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage, IssueStatus, type Issue } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { loadWorkflow, loadAgentConfig } from './workflow-loader';
import { getAgentTimeoutConfig, load as loadConfig } from '../config/config-loader';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt, buildReviewSelfCheckPrompt, buildAutoFixPrompt, buildReVerifyPrompt, type ArtifactType } from '../agents/artifact-prompt';
import { formatAgentPrompt } from '../agents/agent-prompt-schema';
import type { IssueRepo } from '../db/issue-repo';
import type { EventBus } from '../services/event-bus';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import type { AgentConfig } from './workflow-loader';
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
  escalateToStage?: Stage;
}

export interface WorkflowControllerOptions {
  artifactManager: ChangeArtifactsManager;
  worktreePath: string;
  issueRepo?: IssueRepo;
  eventBus?: EventBus;
  projectId?: string;
  checkpointRepo?: PipelineCheckpointRepo;
  signal?: AbortSignal;
  worktreeManager?: any;
  projectRepo?: any;
  onChildProcess?: (proc: any) => void;
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
  private signal?: AbortSignal;
  private worktreeManager?: any;
  private projectRepo?: any;
  private _onChildProcess?: WorkflowControllerOptions['onChildProcess'];
  private _agentConfigCache?: AgentConfig;

  constructor(options: WorkflowControllerOptions) {
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.projectId = options.projectId;
    this.checkpointRepo = options.checkpointRepo;
    this.signal = options.signal;
    this.worktreeManager = options.worktreeManager;
    this.projectRepo = options.projectRepo;
    this._onChildProcess = options.onChildProcess;
  }

  getCheckpointRepo(): PipelineCheckpointRepo | undefined {
    return this.checkpointRepo;
  }

  private getAgentConfig(): AgentConfig {
    if (!this._agentConfigCache) {
      this._agentConfigCache = loadAgentConfig(this.worktreePath);
    }
    return this._agentConfigCache;
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

        const prompt = buildArtifactPrompt(round.type as ArtifactType, issue, changeDir, this.getAgentConfig());
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

          const retryPrompt = formatAgentPrompt({
            role: `Create the ${round.type} artifact for this change`,
            task: `The artifact file was not found. You MUST create it now.\n\nUse the write_file tool to write the ${round.type} artifact to:\n${round.outputPath}\n\nThis is a retry. The pipeline cannot continue without this file.`,
            contract: `Write to: ${round.outputPath}`,
          });

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

      const selfReviewPrompt = buildSelfReviewPrompt(issue, changeDir, this.getAgentConfig());
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

    if (this.signal?.aborted) {
      return {
        completed: false,
        stage: issue.stage,
        gateRequired: false,
        message: 'Agent stopped by user',
      };
    }

    const acpOptsWithSignal: AcpConnectionOptions = {
      ...acpOptions,
      signal: this.signal,
    };

    let currentIssue = issue;

    while (currentIssue.stage !== Stage.Done) {
      if (this.signal?.aborted) {
        return {
          completed: false,
          stage: currentIssue.stage,
          gateRequired: false,
          message: 'Agent stopped by user',
        };
      }

      switch (currentIssue.stage) {
        case Stage.Draft:
        case Stage.Plan: {
          const planResult = await this.runPlanStage(currentIssue, acpOptsWithSignal);
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
          const buildResult = await this.runPipelineBuildStage(currentIssue, acpOptsWithSignal);
          if (!buildResult.success) {
            return {
              completed: false,
              stage: Stage.Build,
              gateRequired: false,
              message: buildResult.message,
            };
          }

          if (this.signal?.aborted) {
            return {
              completed: false,
              stage: Stage.Build,
              gateRequired: false,
              message: 'Agent stopped by user',
            };
          }

          currentIssue = this.issueRepo.updateStage(currentIssue.id, Stage.Check)!;
          break;
        }

        case Stage.Check: {
          const checkResult = await this.runPipelineCheckStage(currentIssue, acpOptsWithSignal);
          if (!checkResult.success) {
            if (checkResult.escalateToStage !== undefined) {
              return {
                completed: false,
                stage: checkResult.escalateToStage,
                gateRequired: false,
                message: checkResult.message ?? 'Check suite failed, escalating to build',
              };
            }
            return {
              completed: false,
              stage: Stage.Check,
              gateRequired: false,
              message: checkResult.message ?? 'Check suite failed',
            };
          }

          this.issueRepo.setApprovalState(currentIssue.id, {
            stage: Stage.Check,
            status: 'awaiting',
            output: checkResult.output,
            requestedAt: new Date().toISOString(),
          });
          this.eventBus.emit('approval_requested', {
            issueId: currentIssue.id,
            projectId: this.projectId ?? currentIssue.projectId,
            stage: Stage.Check,
          });

          return {
            completed: false,
            stage: Stage.Check,
            gateRequired: true,
            message: checkResult.message ?? 'Check suite completed, awaiting approval',
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

  private getBuildStageTimeoutMs(taskCount: number): number {
    const agentConfig = getAgentTimeoutConfig(loadConfig());
    return agentConfig.taskTimeout * taskCount * 1000;
  }

  private writeLog(workflowLogRepo: WorkflowLogRepo | undefined, issueId: string, eventType: string, data: object): void {
    if (!workflowLogRepo) return;
    try {
      workflowLogRepo.insert(issueId, null, eventType, data);
    } catch (e) {
      log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
    }
  }

  async runPipelineBuildStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
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
      stageTimeoutMs: this.getBuildStageTimeoutMs(total),
      agentConfig: this.getAgentConfig(),
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
      elapsedMs: duration,
    });

    const hadCheckpoint = activeCompletedTaskIds.length > 0;

    if (result.completed === 0 && result.total > 0 && !hadCheckpoint) {
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

  private async runPipelineCheckStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const checks: any[] = [];
    const overallStartTime = Date.now();
    const workflowLogRepo = acpOptions.workflowLogRepo;

    this.emitSafe('check_started', {
      issueId: issue.id,
      projectId: this.projectId ?? issue.projectId,
      issueNumber: issue.number,
    });
    this.writeLog(workflowLogRepo, issue.id, 'check_started', { issueNumber: issue.number });

    this.emitSafe('check_update', {
      issueId: issue.id,
      projectId: this.projectId ?? issue.projectId,
      checkName: 'build-test',
      status: 'running',
    });
    this.writeLog(workflowLogRepo, issue.id, 'check_update', { checkName: 'build-test', status: 'running' });

    const buildTestResult = await this.runBuildTestCheck(issue, acpOptions);
    checks.push(buildTestResult);

    this.emitSafe('check_update', {
      issueId: issue.id,
      projectId: this.projectId ?? issue.projectId,
      checkName: 'build-test',
      status: buildTestResult.status,
      duration: buildTestResult.duration,
    });

    if (buildTestResult.status === 'failed') {
      const overallDuration = Date.now() - overallStartTime;
      const suiteOutput = {
        checks,
        overallResult: 'failed',
      };

      log.warn('Check suite stopped: Build & Test failed', {
        issueNumber: issue.number,
        duration: overallDuration,
      });

      return {
        success: false,
        requiresApproval: false,
        output: suiteOutput,
        message: `Check suite stopped: Build & Test failed — ${buildTestResult.summary}`,
      };
    }

    const { loadChecksConfig, DEFAULT_CHECKS_CONFIG } = await import('./workflow-loader');
    const workflow = loadWorkflow(this.worktreePath);
    const checksConfig = typeof workflow === 'string' ? DEFAULT_CHECKS_CONFIG : loadChecksConfig(workflow);

    if (checksConfig.ffMerge.enabled) {
      this.emitSafe('check_update', {
        issueId: issue.id,
        projectId: this.projectId ?? issue.projectId,
        checkName: 'merge-ready',
        status: 'running',
      });
      this.writeLog(workflowLogRepo, issue.id, 'check_update', { checkName: 'merge-ready', status: 'running' });

      const mergeReadyResult = await this.runMergeReadyCheck(issue);
      checks.push(mergeReadyResult);

      this.emitSafe('check_update', {
        issueId: issue.id,
        projectId: this.projectId ?? issue.projectId,
        checkName: 'merge-ready',
        status: mergeReadyResult.status,
        duration: mergeReadyResult.duration,
      });
      this.writeLog(workflowLogRepo, issue.id, 'check_update', { checkName: 'merge-ready', status: mergeReadyResult.status, duration: mergeReadyResult.duration });
    }

    if (checksConfig.aiReview.enabled) {
      this.emitSafe('check_update', {
        issueId: issue.id,
        projectId: this.projectId ?? issue.projectId,
        checkName: 'ai-review',
        status: 'running',
      });
      this.writeLog(workflowLogRepo, issue.id, 'check_update', { checkName: 'ai-review', status: 'running' });

      const { result: aiReviewResult, escalateToStage } = await this.runAiReviewCheck(issue, acpOptions);
      checks.push(aiReviewResult);

      this.emitSafe('check_update', {
        issueId: issue.id,
        projectId: this.projectId ?? issue.projectId,
        checkName: 'ai-review',
        status: aiReviewResult.status,
        duration: aiReviewResult.duration,
        autoFixed: aiReviewResult.autoFixed,
        verdict: aiReviewResult.verdict,
      });
      this.writeLog(workflowLogRepo, issue.id, 'check_update', { checkName: 'ai-review', status: aiReviewResult.status, duration: aiReviewResult.duration, autoFixed: aiReviewResult.autoFixed, verdict: aiReviewResult.verdict });

      if (aiReviewResult.status === 'failed') {
        const suiteOutput = { checks, overallResult: 'failed' };

        if (escalateToStage !== undefined) {
          this.checkpointRepo?.upsert(issue.number, 'review', ['no-auto-fix'], null);
          this.writeLog(workflowLogRepo, issue.id, 'check_failed', { checks, reason: aiReviewResult.summary, escalatedTo: escalateToStage });
          return {
            success: false,
            requiresApproval: false,
            output: suiteOutput,
            escalateToStage,
            message: aiReviewResult.summary ?? 'AI review failed and auto-fix exhausted',
          };
        }

        this.writeLog(workflowLogRepo, issue.id, 'check_completed', { checks, overallResult: 'failed-awaiting-approval' });
        return {
          success: true,
          requiresApproval: true,
          output: suiteOutput,
          message: 'Check suite completed with AI review failures, awaiting user approval',
        };
      }
    }

    const suiteOutput = { checks, overallResult: 'passed' };
    this.writeLog(workflowLogRepo, issue.id, 'check_completed', { checks, duration: Date.now() - overallStartTime });
    return {
      success: true,
      requiresApproval: true,
      output: suiteOutput,
      message: 'Check suite completed, awaiting user approval',
    };
  }

  private async runBuildTestCheck(issue: Issue, acpOptions: AcpConnectionOptions): Promise<any> {
    const { loadChecksConfig, DEFAULT_CHECKS_CONFIG } = await import('./workflow-loader');
    const workflow = loadWorkflow(this.worktreePath);
    const config = typeof workflow === 'string' ? DEFAULT_CHECKS_CONFIG : loadChecksConfig(workflow);
    const { command, timeout, autoFix, maxFixAttempts } = config.buildTest;

    const startTime = Date.now();

    for (let attempt = 0; attempt <= (autoFix ? maxFixAttempts : 0); attempt++) {
      log.info('Build & Test check running', {
        issueNumber: issue.number,
        command,
        timeout,
        attempt: attempt + 1,
        autoFix,
      });

      try {
        const { stdout, stderr } = await execFileAsync(command, [], {
          cwd: this.worktreePath,
          timeout,
          maxBuffer: 10 * 1024 * 1024,
          shell: true,
        });

        const duration = Date.now() - startTime;
        log.info('Build & Test check passed', { issueNumber: issue.number, duration, attempt: attempt + 1 });

        return {
          name: 'build-test',
          status: 'passed',
          duration,
          autoFixed: attempt > 0,
          summary: `Build & test passed${attempt > 0 ? ` (auto-fixed on attempt ${attempt + 1})` : ' on first attempt'}`,
          buildLog: truncateLog(stdout + '\n' + stderr, 50000),
        };
      } catch (err: any) {
        const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
        const isTimeout = err.killed === true;
        const duration = Date.now() - startTime;

        if (!autoFix || attempt >= maxFixAttempts) {
          log.warn('Build & Test check failed, no more attempts', {
            issueNumber: issue.number,
            attempt: attempt + 1,
            autoFix,
            maxFixAttempts,
            isTimeout,
          });

          return {
            name: 'build-test',
            status: 'failed',
            duration,
            summary: isTimeout
              ? `Build & test timed out after ${timeout}ms`
              : `Build & test failed: ${err.message ?? 'unknown error'}`,
            buildLog: truncateLog(output, 50000),
          };
        }

        log.info('Build & Test check failed, spawning coder agent for auto-fix', {
          issueNumber: issue.number,
          attempt: attempt + 1,
          maxFixAttempts,
        });

        const fixResult = await this.spawnBuildTestFixAgent(issue, output, acpOptions);
        if (!fixResult.success) {
          log.warn('Build & Test auto-fix agent failed', {
            issueNumber: issue.number,
            attempt: attempt + 1,
            error: fixResult.error,
          });
        }
      }
    }

    const duration = Date.now() - startTime;
    return {
      name: 'build-test',
      status: 'failed',
      duration,
      summary: `Build & test failed after ${maxFixAttempts} auto-fix attempt(s)`,
    };
  }

  private async spawnBuildTestFixAgent(
    issue: Issue,
    buildOutput: string,
    parentAcpOptions: AcpConnectionOptions,
  ): Promise<{ success: boolean; error?: string }> {
    let conn: import('../agent-runtime/acp-session').AcpConnection | undefined;
    try {
      const acpOptions: AcpConnectionOptions = {
        cwd: this.worktreePath,
        issueNumber: issue.number,
        issueId: issue.id,
        projectId: this.projectId ?? issue.projectId,
        model: issue.model ?? undefined,
        workflowLogRepo: parentAcpOptions.workflowLogRepo,
        eventBus: parentAcpOptions.eventBus,
        signal: parentAcpOptions.signal,
      };

      conn = await createAcpConnection(acpOptions);

      const prompt = formatAgentPrompt({
        role: 'Fix build/test errors',
        task: [
          `The build/test command failed in the worktree at: ${this.worktreePath}`,
          '',
          '## Build/Test Error Output',
          '```',
          truncateLog(buildOutput, 30000),
          '```',
        ].join('\n'),
        contract: [
          'Fix ONLY the errors shown above — do NOT modify unrelated files.',
          'After fixing, do NOT run the build/test command yourself — the pipeline will re-run it.',
        ].join('\n'),
      });

      const result = await conn.prompt(prompt);
      await conn.close();
      conn = undefined;

      if (!result.success) {
        return { success: false, error: result.error ?? 'unknown error' };
      }
      return { success: true };
    } catch (err) {
      if (conn) {
        try { await conn.close(); } catch { /* ignore */ }
      }
      return { success: false, error: err instanceof Error ? err.message : String(err) };
    }
  }

  private async runMergeReadyCheck(issue: Issue): Promise<any> {
    const startTime = Date.now();

    if (!this.worktreeManager || !this.projectRepo) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check skipped: worktreeManager or projectRepo not available', {
        issueNumber: issue.number,
      });
      return {
        name: 'merge-ready',
        status: 'passed',
        duration,
        summary: 'Merge Ready: skipped (worktreeManager or projectRepo not configured)',
      };
    }

    const project = this.projectRepo.findById(issue.projectId);
    if (!project) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check skipped: project not found', {
        issueNumber: issue.number,
        projectId: issue.projectId,
      });
      return {
        name: 'merge-ready',
        status: 'passed',
        duration,
        summary: 'Merge Ready: skipped (project not found)',
      };
    }

    try {
      const canFF = await this.worktreeManager.canFastForward(
        project.path,
        project.name,
        issue.number,
        project.baseBranch,
      );
      const duration = Date.now() - startTime;

      return {
        name: 'merge-ready',
        status: 'passed',
        duration,
        summary: canFF ? 'Merge Ready: yes' : 'Merge Ready: needs rebase',
      };
    } catch (err) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check error', {
        issueNumber: issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      return {
        name: 'merge-ready',
        status: 'passed',
        duration,
        summary: `Merge Ready: check error (${err instanceof Error ? err.message : String(err)})`,
      };
    }
  }

  private async runAiReviewCheck(issue: Issue, acpOptions: AcpConnectionOptions): Promise<{ result: any; escalateToStage?: Stage }> {
    const startTime = Date.now();

    const changeDir = this.artifactManager.getChangeDir(issue.number);
    if (!changeDir) {
      const duration = Date.now() - startTime;
      return { result: { name: 'ai-review', status: 'failed', duration, summary: `Change directory not found for issue #${issue.number}` } };
    }

    const hasNoAutoFix = this.checkpointRepo?.get(issue.number, 'review')?.completedSteps.includes('no-auto-fix') ?? false;

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

    let conn: import('../agent-runtime/acp-session').AcpConnection | undefined;

    try {
      conn = await createAcpConnection(reviewAcpOptions);

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

      const reviewerPrompt = buildReviewerPrompt(issue, changeDir, this.getAgentConfig());
      const result = await conn.prompt(reviewerPrompt);

      if (!result.success) {
        log.error('Review stage round failed', { roundType: 'review', error: result.error });
        await conn.close();
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'failed', duration, summary: `AI review failed: ${result.error ?? 'unknown error'}` } };
      }

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

      const selfCheckPrompt = buildReviewSelfCheckPrompt(issue, changeDir, this.getAgentConfig());
      const selfCheckResult = await conn.prompt(selfCheckPrompt);

      if (!selfCheckResult.success) {
        log.error('Review stage self-check failed', { error: selfCheckResult.error });
        await conn.close();
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'failed', duration, summary: `AI review self-check failed: ${selfCheckResult.error ?? 'unknown error'}` } };
      }

      await conn.close();
      conn = undefined;

      const reviewReport = readReportFile(changeDir, 'review.md') ?? selfCheckResult.text;

      if (!reviewReport || reviewReport.trim().length === 0) {
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'failed', duration, summary: 'Review report is empty after self-check' } };
      }

      const parsedResult = parseVerdict(reviewReport);

      if (parsedResult === 'PASS') {
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'passed', duration, summary: 'AI code review passed', verdict: 'PASS', reviewReport } };
      }

      if (hasNoAutoFix) {
        log.info('no-auto-fix checkpoint present, skipping auto-fix loop', { issueNumber: issue.number });
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'failed', duration, summary: 'AI code review found issues (auto-fix skipped)', verdict: 'FAIL', reviewReport } };
      }

      const fixSuggestions = extractFixSuggestions(reviewReport);
      if (!fixSuggestions) {
        log.info('No Fix Suggestions found, skipping auto-fix', { issueNumber: issue.number });
        const duration = Date.now() - startTime;
        return { result: { name: 'ai-review', status: 'failed', duration, summary: 'AI code review found issues (no auto-fixable suggestions)', verdict: 'FAIL', reviewReport } };
      }

      const autoFixResult = await this.runAutoFixLoop(issue, acpOptions, changeDir, reviewReport, fixSuggestions, roundState);

      const duration = Date.now() - startTime;

      if (autoFixResult.success) {
        const updatedReport = typeof autoFixResult.output === 'object' && autoFixResult.output !== null
          ? (autoFixResult.output as any).reviewReport ?? reviewReport
          : reviewReport;

        return { result: { name: 'ai-review', status: 'passed', duration, autoFixed: true, summary: 'AI code review passed after auto-fix', verdict: 'PASS', reviewReport: updatedReport } };
      }

      if (autoFixResult.escalateToStage !== undefined) {
        return {
          result: { name: 'ai-review', status: 'failed', duration, summary: autoFixResult.message ?? 'Auto-fix loop exhausted', verdict: 'FAIL', reviewReport },
          escalateToStage: autoFixResult.escalateToStage,
        };
      }

      return { result: { name: 'ai-review', status: 'failed', duration, summary: autoFixResult.message ?? 'AI code review found issues after auto-fix attempts', verdict: 'FAIL', reviewReport } };
    } catch (err) {
      if (conn) {
        try { await conn.close(); } catch { /* ignore */ }
      }
      const duration = Date.now() - startTime;
      return { result: { name: 'ai-review', status: 'failed', duration, summary: `AI review error: ${err instanceof Error ? err.message : String(err)}` } };
    }
  }

  private buildReviewAcpOptions(
    issue: Issue,
    acpOptions: AcpConnectionOptions,
    roundState: { type: string; index: number },
    _connRef: { current: import('../agent-runtime/acp-session').AcpConnection | undefined },
  ): any {
    return {
      ...acpOptions,
      executionId: `review-${issue.number}`,
      model: issue.model ?? undefined,
      onProcessSpawned: (proc: any) => { this._onChildProcess?.(proc); },
      onSessionUpdate: (_notification: any) => {
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
  }

  private async runReviewRound(
    issue: Issue,
    acpOptions: AcpConnectionOptions,
    roundType: string,
    roundIndex: number,
    prompt: string,
  ): Promise<{ success: boolean; text?: string; error?: string }> {
    const issueId = String(acpOptions.issueNumber ?? acpOptions.issueId ?? '');
    const projectId = this.projectId ?? issue.projectId;

    log.info('Review stage round', { roundType, roundIndex, issueNumber: issue.number });

    this.emitSafe('plan_round_start', {
      issueId,
      projectId,
      roundType,
      roundLabel: roundType,
      roundIndex,
    });

    let conn: import('../agent-runtime/acp-session').AcpConnection | undefined;
    try {
      conn = await createAcpConnection(acpOptions);
      const result = await conn.prompt(prompt);
      await conn.close();
      conn = undefined;

      if (!result.success) {
        log.error('Review round failed', { roundType, roundIndex, error: result.error });
        return { success: false, error: result.error ?? 'unknown error' };
      }

      return { success: true, text: result.text };
    } catch (err) {
      if (conn) {
        try { await conn.close(); } catch { /* ignore */ }
      }
      return { success: false, error: err instanceof Error ? err.message : String(err) };
    }
  }

  private async runAutoFixLoop(
    issue: Issue,
    acpOptions: AcpConnectionOptions,
    changeDir: string,
    reviewReport: string,
    _fixSuggestions: string,
    roundState: { type: string; index: number },
  ): Promise<StageResult> {
    const MAX_ATTEMPTS = 2;
    const connRef = { current: undefined as import('../agent-runtime/acp-session').AcpConnection | undefined };

    for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
      const autoFixRoundIndex = 2 + attempt * 2;
      const reVerifyRoundIndex = 3 + attempt * 2;

      roundState.type = 'auto-fix';
      roundState.index = autoFixRoundIndex;

      const autoFixPrompt = buildAutoFixPrompt(issue, changeDir, reviewReport, 'review.md', this.getAgentConfig());
      const autoFixAcpOptions = this.buildReviewAcpOptions(issue, acpOptions, roundState, connRef);
      const autoFixResult = await this.runReviewRound(issue, autoFixAcpOptions, 'auto-fix', autoFixRoundIndex, autoFixPrompt);

      if (!autoFixResult.success) {
        log.warn('Auto-fix round failed, counting as attempt', {
          attempt: attempt + 1,
          error: autoFixResult.error,
          issueNumber: issue.number,
        });
        continue;
      }

      roundState.type = 're-verify';
      roundState.index = reVerifyRoundIndex;

      const reVerifyPrompt = buildReVerifyPrompt(issue, changeDir, reviewReport, this.getAgentConfig());
      const reVerifyAcpOptions = this.buildReviewAcpOptions(issue, acpOptions, roundState, connRef);
      const reVerifyResult = await this.runReviewRound(issue, reVerifyAcpOptions, 're-verify', reVerifyRoundIndex, reVerifyPrompt);

      if (!reVerifyResult.success) {
        log.warn('Re-verify round failed', {
          attempt: attempt + 1,
          error: reVerifyResult.error,
          issueNumber: issue.number,
        });
        continue;
      }

      const updatedReport = readReportFile(changeDir, 'review.md') ?? reVerifyResult.text ?? '';
      const result = parseVerdict(updatedReport);

      if (result === 'PASS') {
        log.info('Auto-fix succeeded', { attempt: attempt + 1, issueNumber: issue.number });
        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Check,
            issueNumber: issue.number,
            reviewReport: updatedReport,
          },
          message: `Review completed with auto-fix (attempt ${attempt + 1}), awaiting user approval`,
        };
      }

      log.info('Re-verify still FAIL after auto-fix attempt', {
        attempt: attempt + 1,
        issueNumber: issue.number,
      });
    }

    log.warn('Auto-fix loop exhausted, escalating to build stage', {
      issueNumber: issue.number,
      maxAttempts: MAX_ATTEMPTS,
    });

    return {
      success: false,
      requiresApproval: false,
      output: null,
      escalateToStage: Stage.Build,
      message: `Auto-fix loop exhausted after ${MAX_ATTEMPTS} attempts, escalating to build stage`,
    };
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

function truncateLog(log: string, maxLength: number): string {
  if (log.length <= maxLength) return log;
  const half = Math.floor(maxLength / 2);
  return log.slice(0, half) + '\n\n...[truncated]...\n\n' + log.slice(-half);
}

const RESULT_RE = /^##\s*Result\s*:\s*(PASS|FAIL)\s*$/im;
const LEGACY_VERDICT_RE = /^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/im;

export function parseResult(content: string): 'PASS' | 'FAIL' | null {
  const match = RESULT_RE.exec(content);
  if (match) return match[1].toUpperCase() as 'PASS' | 'FAIL';
  const legacyMatch = LEGACY_VERDICT_RE.exec(content);
  if (legacyMatch) {
    log.warn('parseResult: matched legacy "## Verdict:" header, update prompt templates to use "## Result:"');
    return legacyMatch[1].toUpperCase() as 'PASS' | 'FAIL';
  }
  return null;
}

const CASE_SENSITIVE_RESULT_RE = /^##\s*Result\s*:\s*(PASS|FAIL)\s*$/m;
const CASE_SENSITIVE_VERDICT_RE = /^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/m;

export function parseVerdict(content: string): 'PASS' | 'FAIL' {
  const match = CASE_SENSITIVE_RESULT_RE.exec(content);
  if (match) return match[1] as 'PASS' | 'FAIL';
  const legacyMatch = CASE_SENSITIVE_VERDICT_RE.exec(content);
  if (legacyMatch) return legacyMatch[1] as 'PASS' | 'FAIL';
  return 'FAIL';
}

export function extractFixSuggestions(content: string): string {
  const match = content.match(/^##\s*Fix\s*Suggestions\s*$/im);
  if (!match) return '';
  const startIdx = match.index! + match[0].length;
  return content.slice(startIdx).trim();
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}

export interface ParsedDimension {
  name: string;
  status: 'PASS' | 'FAIL';
  issues?: string[];
}

const RESULT_RE = /^##\s*Result\s*:\s*(PASS|FAIL)\s*$/im;
const LEGACY_VERDICT_RE = /^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/im;

export function parseVerdict(content: string): 'PASS' | 'FAIL' | null {
  if (!content || content.trim() === '') {
    return 'FAIL';
  }
  const match = RESULT_RE.exec(content);
  if (match) return match[1].toUpperCase() as 'PASS' | 'FAIL';
  const legacyMatch = LEGACY_VERDICT_RE.exec(content);
  if (legacyMatch) {
    log.warn('parseResult: matched legacy "## Verdict:" header, update prompt templates to use "## Result:"');
    return legacyMatch[1].toUpperCase() as 'PASS' | 'FAIL';
  }
  return 'FAIL';
}

export function parseDimensions(content: string): ParsedDimension[] {
  const dimensions: ParsedDimension[] = [];
  const matches: Array<{ name: string; status: 'PASS' | 'FAIL'; index: number; endIndex: number }> = [];

  const DIMENSION_RE = /^###\s+(\w[\w\s]*?):\s*(PASS|FAIL)\s*$/gim;
  let m: RegExpExecArray | null;
  while ((m = DIMENSION_RE.exec(content)) !== null) {
    matches.push({
      name: m[1].trim(),
      status: m[2].toUpperCase() as 'PASS' | 'FAIL',
      index: m.index + m[0].length,
      endIndex: -1,
    });
  }

  for (let i = 0; i < matches.length; i++) {
    const start = matches[i].index;
    const end = i + 1 < matches.length ? matches[i + 1].index - matches[i + 1].name.length - matches[i + 1].status.length - 6 : content.length;
    const section = content.slice(start, end);

    const issues = section
      .split('\n')
      .filter(line => /^[-*]\s+/.test(line.trim()))
      .map(line => line.trim().replace(/^[-*]\s+/, ''));

    dimensions.push({
      name: matches[i].name,
      status: matches[i].status,
      ...(issues.length > 0 ? { issues } : {}),
    });
  }

  return dimensions;
}

export function parseResult(content: string): 'PASS' | 'FAIL' | null {
  return parseVerdict(content);
}

export function extractFixSuggestions(content: string): string {
  const match = content.match(/^##\s*Fix\s*Suggestions\s*$/im);
  if (!match) return '';
  const startIdx = match.index! + match[0].length;
  return content.slice(startIdx).trim();
}
