import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import type { ChildProcess } from 'child_process';
import { promisify } from 'util';
import { Stage, IssueStatus, MergeState, type Issue } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { loadWorkflow } from './workflow-loader';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt, buildReviewSelfCheckPrompt, buildAutoFixPrompt, buildReVerifyPrompt, type ArtifactType } from '../agents/artifact-prompt';
import type { IssueRepo } from '../db/issue-repo';
import type { CommentRepo } from '../db/comment-repo';
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
  escalateToStage?: Stage;
}

export interface MergeBackResult {
  success: boolean;
  message: string;
}

export interface WorkflowControllerOptions {
  artifactManager: ChangeArtifactsManager;
  worktreePath: string;
  issueRepo?: IssueRepo;
  eventBus?: EventBus;
  projectId?: string;
  checkpointRepo?: PipelineCheckpointRepo;
  commentRepo?: CommentRepo;
  onProgress?: (update: { stage?: string; roundType?: string; roundIndex?: number; taskProgress?: { completed: number; total: number } | null }) => void;
  onChildProcess?: (proc: ChildProcess) => void;
  mergeBackFn?: (issueNumber: number) => Promise<MergeBackResult>;
  onMergeConflictFn?: (issueNumber: number) => Promise<void>;
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
  private commentRepo?: CommentRepo;
  private _onProgress?: WorkflowControllerOptions['onProgress'];
  private _onChildProcess?: WorkflowControllerOptions['onChildProcess'];
  private mergeBackFn?: (issueNumber: number) => Promise<MergeBackResult>;
  private onMergeConflictFn?: (issueNumber: number) => Promise<void>;

  constructor(options: WorkflowControllerOptions) {
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.projectId = options.projectId;
    this.checkpointRepo = options.checkpointRepo;
    this.commentRepo = options.commentRepo;
    this._onProgress = options.onProgress;
    this._onChildProcess = options.onChildProcess;
    this.mergeBackFn = options.mergeBackFn;
    this.onMergeConflictFn = options.onMergeConflictFn;
  }

  protected emitProgress(update: Parameters<NonNullable<WorkflowControllerOptions['onProgress']>>[0]): void {
    this._onProgress?.(update);
  }

  getCheckpointRepo(): PipelineCheckpointRepo | undefined {
    return this.checkpointRepo;
  }

  getCommentRepo(): CommentRepo | undefined {
    return this.commentRepo;
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

      const resumeRoundTypes = ['proposal', 'specs', 'design', 'tasks', 'self-review'];
      const resumeCompleted = completedSteps.filter(s => resumeRoundTypes.includes(s)).length;
      const lastCompletedRoundType = [...completedSteps].reverse().find(s => resumeRoundTypes.includes(s));
      const lastCompletedIndex = lastCompletedRoundType ? resumeRoundTypes.indexOf(lastCompletedRoundType) : -1;
      this.emitProgress({
        stage: 'plan',
        roundType: lastCompletedRoundType ?? 'proposal',
        roundIndex: lastCompletedIndex >= 0 ? lastCompletedIndex : 0,
        taskProgress: { completed: resumeCompleted, total: 5 },
      });
    }

    const issueId = String(acpOptions.issueNumber ?? acpOptions.issueId ?? '');
    const projectId = this.projectId ?? issue.projectId;

    const rounds: PlanRoundConfig[] = [
      { type: 'proposal', verify: () => fs.existsSync(path.join(changeDir, 'proposal.md')), label: 'proposal.md', outputPath: path.join(changeDir, 'proposal.md') },
      { type: 'specs', verify: () => fs.existsSync(path.join(changeDir, 'specs')), label: 'specs/', outputPath: path.join(changeDir, 'specs') },
      { type: 'design', verify: () => fs.existsSync(path.join(changeDir, 'design.md')), label: 'design.md', outputPath: path.join(changeDir, 'design.md') },
      { type: 'tasks', verify: () => fs.existsSync(path.join(changeDir, 'tasks.json')), label: 'tasks.json', outputPath: path.join(changeDir, 'tasks.json') },
    ];

    const roundState = { type: '', index: 0 };
    let conn: AcpConnection | undefined;

    const planAcpOptions: AcpConnectionOptions = {
      ...acpOptions,
      stage: Stage.Plan,
      executionId: `plan-${issue.number}`,
      model: issue.model ?? undefined,
      onProcessSpawned: (proc) => { this._onChildProcess?.(proc); },
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
            acpSessionId: conn?.acpSessionId,
            coderSessionId: conn?.coderSessionId,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', { error: e instanceof Error ? e.message : String(e) });
        }
      },
    };

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
              issueId,
              projectId,
              roundType: round.type,
              roundLabel: round.label,
              roundIndex: index,
              acpSessionId: conn?.acpSessionId,
              coderSessionId: conn?.coderSessionId,
            });
          } catch (e) {
            log.warn('eventBus.emit failed for plan_round_start', { error: e instanceof Error ? e.message : String(e) });
          }
        }

        this.emitProgress({ stage: 'plan', roundType: round.type, roundIndex: index, taskProgress: { completed: completedSteps.filter(s => ['proposal', 'specs', 'design', 'tasks', 'self-review'].includes(s)).length, total: 5 } });

        const roundStartTime = Date.now();
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

        this.emitPlanRoundComplete({
          issueId,
          projectId,
          roundType: round.type,
          roundLabel: round.label,
          roundIndex: index,
          startTime: roundStartTime,
        });

        const completedCount = completedSteps.filter(s => ['proposal', 'specs', 'design', 'tasks', 'self-review'].includes(s)).length;
        this.emitProgress({ stage: 'plan', roundType: round.type, roundIndex: index, taskProgress: { completed: completedCount, total: 5 } });
      }

      // self-review round
      roundState.type = 'self-review';
      roundState.index = rounds.length;

      log.info('Plan stage self-review round', { issueNumber: issue.number });

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId,
            projectId,
            roundType: 'self-review',
            roundLabel: 'self-review',
            roundIndex: rounds.length,
            acpSessionId: conn?.acpSessionId,
            coderSessionId: conn?.coderSessionId,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 'self-review', error: e instanceof Error ? e.message : String(e) });
        }
      }

      this.emitProgress({ stage: 'plan', roundType: 'self-review', roundIndex: rounds.length, taskProgress: { completed: completedSteps.filter(s => ['proposal', 'specs', 'design', 'tasks', 'self-review'].includes(s)).length, total: 5 } });

      const selfReviewStartTime = Date.now();
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

      const selfReviewReport = readReportFile(changeDir, 'self-review.md') ?? selfReviewResult.text;

      const verdict = selfReviewReport ? parseVerdict(selfReviewReport) : 'FAIL';

      this.emitPlanRoundComplete({
        issueId,
        projectId,
        roundType: 'self-review',
        roundLabel: 'self-review',
        roundIndex: rounds.length,
        startTime: selfReviewStartTime,
        verdict: verdict === 'PASS' || verdict === 'FAIL' ? verdict : undefined,
      });

      if (verdict === 'PASS') {
        this.emitProgress({ stage: 'plan', roundType: 'self-review', roundIndex: rounds.length, taskProgress: { completed: 5, total: 5 } });
        await conn.close();
        this.checkpointRepo?.delete(issue.number, 'plan');
        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Plan,
            issueNumber: issue.number,
            selfReviewNotes: selfReviewReport,
            verdict,
          },
          message: 'Plan completed, awaiting user approval',
        };
      }

      this.emitProgress({ stage: 'plan', roundType: 'self-review', roundIndex: rounds.length, taskProgress: { completed: 4, total: 5 } });

      // Verdict FAIL → auto-fix on same connection
      roundState.type = 'auto-fix';
      roundState.index = rounds.length + 1;
      log.info('Plan stage auto-fix round', { issueNumber: issue.number });

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId,
            projectId,
            roundType: 'auto-fix',
            roundLabel: 'auto-fix',
            roundIndex: rounds.length + 1,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 'auto-fix', error: e instanceof Error ? e.message : String(e) });
        }
      }

      const autoFixStartTime = Date.now();
      const autoFixPrompt = buildAutoFixPrompt(issue, changeDir, selfReviewReport ?? '', 'self-review.md');
      const autoFixResult = await conn.prompt(autoFixPrompt);

      if (!autoFixResult.success) {
        log.error('Plan stage auto-fix prompt failed', { error: autoFixResult.error });
        await conn.close();
        this.checkpointRepo?.delete(issue.number, 'plan');
        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Plan,
            issueNumber: issue.number,
            selfReviewNotes: selfReviewReport,
            verdict,
          },
          message: `Auto-fix failed: ${autoFixResult.error ?? 'unknown error'}. Awaiting user approval`,
        };
      }

      this.emitPlanRoundComplete({
        issueId,
        projectId,
        roundType: 'auto-fix',
        roundLabel: 'auto-fix',
        roundIndex: rounds.length + 1,
        startTime: autoFixStartTime,
      });

      // Close old connection, open new one for full re-self-review
      await conn.close();

      roundState.type = 're-self-review';
      roundState.index = rounds.length + 2;
      log.info('Plan stage re-self-review round on new connection', { issueNumber: issue.number });

      conn = await createAcpConnection(planAcpOptions);

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId,
            projectId,
            roundType: 're-self-review',
            roundLabel: 're-self-review',
            roundIndex: rounds.length + 2,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_round_start', { roundType: 're-self-review', error: e instanceof Error ? e.message : String(e) });
        }
      }

      const reSelfReviewStartTime = Date.now();
      const reSelfReviewPrompt = buildSelfReviewPrompt(issue, changeDir);
      const reSelfReviewResult = await conn.prompt(reSelfReviewPrompt);

      if (!reSelfReviewResult.success) {
        log.error('Plan stage re-self-review failed', { error: reSelfReviewResult.error });
        await conn.close();
        this.checkpointRepo?.delete(issue.number, 'plan');
        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Plan,
            issueNumber: issue.number,
            selfReviewNotes: selfReviewReport,
            verdict,
          },
          message: `Auto-fix succeeded but re-self-review failed: ${reSelfReviewResult.error ?? 'unknown error'}. Awaiting user approval`,
        };
      }

      await conn.close();
      this.checkpointRepo?.delete(issue.number, 'plan');

      const recheckReport = readReportFile(changeDir, 'self-review.md') ?? reSelfReviewResult.text;
      const recheckVerdict = recheckReport ? parseVerdict(recheckReport) : 'FAIL';

      this.emitPlanRoundComplete({
        issueId,
        projectId,
        roundType: 're-self-review',
        roundLabel: 're-self-review',
        roundIndex: rounds.length + 2,
        startTime: reSelfReviewStartTime,
        verdict: recheckVerdict === 'PASS' || recheckVerdict === 'FAIL' ? recheckVerdict : undefined,
      });

      this.emitProgress({ stage: 'plan', roundType: 're-self-review', roundIndex: rounds.length + 2, taskProgress: { completed: 5, total: 5 } });

      if (recheckVerdict === 'PASS') {
        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Plan,
            issueNumber: issue.number,
            selfReviewNotes: recheckReport,
            verdict: recheckVerdict,
          },
          message: 'Auto-fix succeeded, re-self-review passed. Awaiting user approval',
        };
      }

      return {
        success: true,
        requiresApproval: true,
        output: {
          stage: Stage.Plan,
          issueNumber: issue.number,
          selfReviewNotes: recheckReport ?? selfReviewReport,
          verdict: recheckVerdict,
        },
        message: 'Auto-fix attempted but re-self-review still FAIL. Awaiting user approval',
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
        case Stage.Backlog:
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
          const isApproved = currentIssue.approvalState?.status === 'approved';
          const isResolving = currentIssue.mergeState === MergeState.Resolving;

          if (isApproved || isResolving) {
            if (!this.mergeBackFn) {
              log.info('Skipping to Done (no mergeBackFn)', {
                issueNumber: currentIssue.number,
                reason: isResolving ? 'conflict resolution' : 'approved',
              });
              currentIssue = this.issueRepo.updateStage(currentIssue.id, Stage.Done)!;
              break;
            }

            const label = isResolving ? 'Resolving' : 'Approved';
            log.info(`${label}: executing mergeBack`, { issueNumber: currentIssue.number });

            try {
              const mergeResult = await this.mergeBackFn(currentIssue.number);

              if (mergeResult.success) {
                log.info(`${label}: mergeBack succeeded`, { issueNumber: currentIssue.number });
                this.issueRepo.setMergeState(currentIssue.id, MergeState.Merged);
                currentIssue = this.issueRepo.updateStage(currentIssue.id, Stage.Done)!;
                break;
              }

              log.warn(`${label}: mergeBack failed`, {
                issueNumber: currentIssue.number,
                message: mergeResult.message,
              });

              if (this.onMergeConflictFn) {
                await this.onMergeConflictFn(currentIssue.number);
                return {
                  completed: false,
                  stage: Stage.Review,
                  gateRequired: false,
                  message: `Merge failed: ${mergeResult.message}. Conflict resolution triggered.`,
                };
              }

              this.issueRepo.setMergeState(currentIssue.id, MergeState.Blocked);
              return {
                completed: false,
                stage: Stage.Review,
                gateRequired: false,
                message: `Merge failed: ${mergeResult.message}`,
              };
            } catch (err) {
              log.error(`${label}: mergeBack threw`, {
                issueNumber: currentIssue.number,
                error: err instanceof Error ? err.message : String(err),
              });

              if (this.onMergeConflictFn) {
                await this.onMergeConflictFn(currentIssue.number);
                return {
                  completed: false,
                  stage: Stage.Review,
                  gateRequired: false,
                  message: `Merge error: ${err instanceof Error ? err.message : String(err)}. Conflict resolution triggered.`,
                };
              }

              this.issueRepo.setMergeState(currentIssue.id, MergeState.Blocked);
              return {
                completed: false,
                stage: Stage.Review,
                gateRequired: false,
                message: `Merge error: ${err instanceof Error ? err.message : String(err)}`,
              };
            }
          }

          const reviewResult = await this.runPipelineReviewStage(currentIssue, acpOptions);
          if (!reviewResult.success) {
            if (reviewResult.escalateToStage !== undefined) {
              log.info('Review stage escalating to build with no-auto-fix checkpoint', {
                issueNumber: currentIssue.number,
                escalateTo: reviewResult.escalateToStage,
              });
              this.checkpointRepo?.upsert(currentIssue.number, 'review', ['no-auto-fix'], null);
              currentIssue = this.issueRepo.updateStage(currentIssue.id, reviewResult.escalateToStage)!;
              break;
            }

            return {
              completed: false,
              stage: Stage.Review,
              gateRequired: false,
              message: reviewResult.message,
            };
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

  private emitPlanRoundComplete(opts: {
    issueId: string;
    projectId: string;
    roundType: string;
    roundLabel: string;
    roundIndex: number;
    startTime: number;
    verdict?: 'PASS' | 'FAIL';
  }): void {
    this.emitSafe('plan_round_complete', {
      issueId: opts.issueId,
      projectId: opts.projectId,
      roundType: opts.roundType,
      roundLabel: opts.roundLabel,
      roundIndex: opts.roundIndex,
      duration: Math.round((Date.now() - opts.startTime) / 1000),
      ...(opts.verdict !== undefined ? { verdict: opts.verdict } : {}),
    });
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
      onProcessSpawned: (proc) => { this._onChildProcess?.(proc); },
      stage: Stage.Build,
      model: issue.model ?? undefined,
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

  private buildReviewAcpOptions(
    issue: Issue,
    acpOptions: AcpConnectionOptions,
    roundState: { type: string; index: number },
    connRef: { current: AcpConnection | undefined },
  ): AcpConnectionOptions {
    return {
      ...acpOptions,
      stage: Stage.Review,
      executionId: `review-${issue.number}`,
      model: issue.model ?? undefined,
      onProcessSpawned: (proc) => { this._onChildProcess?.(proc); },
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
            acpSessionId: connRef.current?.acpSessionId,
            coderSessionId: connRef.current?.coderSessionId,
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

    let conn: AcpConnection | undefined;
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
    fixSuggestions: string,
    roundState: { type: string; index: number },
  ): Promise<StageResult> {
    const MAX_ATTEMPTS = 2;
    const connRef = { current: undefined as AcpConnection | undefined };

    for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
      const autoFixRoundIndex = 2 + attempt * 2;
      const reVerifyRoundIndex = 3 + attempt * 2;

      roundState.type = 'auto-fix';
      roundState.index = autoFixRoundIndex;

      const autoFixPrompt = buildAutoFixPrompt(issue, changeDir, reviewReport, 'review.md');
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

      const reVerifyPrompt = buildReVerifyPrompt(issue, changeDir, reviewReport);
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
      const updatedDimensions = parseDimensions(updatedReport);

      if (result === 'PASS') {
        log.info('Auto-fix succeeded', { attempt: attempt + 1, issueNumber: issue.number });

        if (fixSuggestions && this.commentRepo) {
          try {
            this.commentRepo.create({
              issueId: issue.id,
              body: `**Auto-fix applied** (attempt ${attempt + 1})\n\n${fixSuggestions}`,
            });
          } catch (e) {
            log.warn('Failed to create auto-fix comment', { error: e instanceof Error ? e.message : String(e) });
          }
        }

        return {
          success: true,
          requiresApproval: true,
          output: {
            stage: Stage.Review,
            issueNumber: issue.number,
            reviewReport: updatedReport,
            verdict: result,
            dimensions: updatedDimensions,
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

    const hasNoAutoFix = this.checkpointRepo?.get(issue.number, 'review')?.completedSteps.includes('no-auto-fix') ?? false;

    const roundState = { type: '', index: 0 };
    const connRef = { current: undefined as AcpConnection | undefined };
    const reviewAcpOptions = this.buildReviewAcpOptions(issue, acpOptions, roundState, connRef);

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(reviewAcpOptions);
      connRef.current = conn;

      // Round 0: review
      roundState.type = 'review';
      roundState.index = 0;

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'review',
            roundLabel: 'review',
            roundIndex: 0,
            acpSessionId: conn?.acpSessionId,
            coderSessionId: conn?.coderSessionId,
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
        return { success: false, requiresApproval: false, output: null, message: `Review failed: ${result.error ?? 'unknown error'}` };
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
            acpSessionId: conn?.acpSessionId,
            coderSessionId: conn?.coderSessionId,
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
        return { success: false, requiresApproval: false, output: null, message: `Review stage failed at self-check: ${selfCheckResult.error ?? 'unknown error'}` };
      }

      await conn.close();
      conn = undefined;
      connRef.current = undefined;

      const reviewReport = readReportFile(changeDir, 'review.md') ?? selfCheckResult.text;

      if (!reviewReport || reviewReport.trim().length === 0) {
        return { success: false, requiresApproval: false, output: null, message: 'Review stage failed: review.md is empty after self-check' };
      }

      const parsedResult = parseVerdict(reviewReport);

      const parsedDimensions = parseDimensions(reviewReport);

      if (parsedResult === 'PASS') {
        return {
          success: true,
          requiresApproval: true,
          output: { stage: Stage.Review, issueNumber: issue.number, reviewReport, verdict: parsedResult, dimensions: parsedDimensions },
          message: 'Review completed, awaiting user approval',
        };
      }

      log.info('Review Result: FAIL, checking auto-fix eligibility', {
        issueNumber: issue.number,
        hasNoAutoFixCheckpoint: hasNoAutoFix,
      });

      if (hasNoAutoFix) {
        log.info('no-auto-fix checkpoint present, skipping auto-fix loop', { issueNumber: issue.number });
        return {
          success: true,
          requiresApproval: true,
          output: { stage: Stage.Review, issueNumber: issue.number, reviewReport, verdict: parsedResult, dimensions: parsedDimensions },
          message: 'Review completed (auto-fix skipped due to prior exhaustion), awaiting user approval',
        };
      }

      const fixSuggestions = extractFixSuggestions(reviewReport);
      if (!fixSuggestions) {
        log.info('No Fix Suggestions found, proceeding to awaiting-user without auto-fix', { issueNumber: issue.number });
        return {
          success: true,
          requiresApproval: true,
          output: { stage: Stage.Review, issueNumber: issue.number, reviewReport, verdict: parsedResult, dimensions: parsedDimensions },
          message: 'Review completed (no auto-fixable suggestions), awaiting user approval',
        };
      }

      return this.runAutoFixLoop(issue, acpOptions, changeDir, reviewReport, fixSuggestions, roundState);
    } catch (err) {
      if (conn) {
        try { await conn.close(); } catch { /* ignore */ }
      }
      return { success: false, requiresApproval: false, output: null, message: `Review stage error: ${err instanceof Error ? err.message : String(err)}` };
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

const RESULT_RE = /^##\s*Result\s*:\s*(PASS|FAIL)\s*$/im;
const LEGACY_VERDICT_RE = /^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/im;

export function parseVerdict(content: string): 'PASS' | 'FAIL' | null {
  const match = RESULT_RE.exec(content);
  if (match) return match[1].toUpperCase() as 'PASS' | 'FAIL';
  const legacyMatch = LEGACY_VERDICT_RE.exec(content);
  if (legacyMatch) {
    log.warn('parseResult: matched legacy "## Verdict:" header, update prompt templates to use "## Result:"');
    return legacyMatch[1].toUpperCase() as 'PASS' | 'FAIL';
  }
  return null;
}

export interface ParsedDimension {
  name: string;
  status: 'PASS' | 'FAIL';
  issues?: string[];
}

const DIMENSION_RE = /^###\s+(\w[\w\s]*?):\s*(PASS|FAIL)\s*$/gim;

export function parseDimensions(content: string): ParsedDimension[] {
  const dimensions: ParsedDimension[] = [];
  const matches: Array<{ name: string; status: 'PASS' | 'FAIL'; index: number; endIndex: number }> = [];

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

export function extractFixSuggestions(content: string): string {
  const match = content.match(/^##\s*Fix\s*Suggestions\s*$/im);
  if (!match) return '';
  const startIdx = match.index! + match[0].length;
  return content.slice(startIdx).trim();
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}
