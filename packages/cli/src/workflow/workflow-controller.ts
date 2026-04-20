import * as fs from 'fs';
import * as path from 'path';
import { Stage, type Issue } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt, type ArtifactType } from '../agents/artifact-prompt';
import type { IssueRepo } from '../db/issue-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';

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

  constructor(options: WorkflowControllerOptions) {
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.projectId = options.projectId;
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

    cleanChangeDir(changeDir);

    const rounds: PlanRoundConfig[] = [
      { type: 'proposal', verify: () => fs.existsSync(path.join(changeDir, 'proposal.md')), label: 'proposal.md' },
      { type: 'specs', verify: () => fs.existsSync(path.join(changeDir, 'specs')), label: 'specs/' },
      { type: 'design', verify: () => fs.existsSync(path.join(changeDir, 'design.md')), label: 'design.md' },
      { type: 'tasks', verify: () => fs.existsSync(path.join(changeDir, 'tasks.json')), label: 'tasks.json' },
    ];

    // roundState is mutated by the for loop and read by onSessionUpdate callback.
    // Safe because JS is single-threaded.
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
        } catch {
          // fire-and-forget
        }
      },
    };

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(planAcpOptions);

      for (const [index, round] of rounds.entries()) {
        roundState.type = round.type;
        roundState.index = index;

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
          } catch {
            // fire-and-forget
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
          log.error('Plan stage artifact not found after round', { artifact: round.label });
          await conn.close();
          return {
            success: false,
            requiresApproval: false,
            output: null,
            message: `Plan stage failed: artifact "${round.label}" not found after generation`,
          };
        }
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
        } catch {
          // fire-and-forget
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

      return {
        success: true,
        requiresApproval: true,
        output: {
          stage: Stage.Plan,
          issueNumber: issue.number,
          selfReviewNotes: selfReviewResult.text,
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
    return { completed: true, stage: Stage.Done, gateRequired: false, message: 'Pipeline completed' };
  }

  private async runPipelineBuildStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const change = detectOpenSpecChange(this.worktreePath, issue);

    if (change) {
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
      });

      const result: RalphLoopResult = await executor.execute(change);

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

    return {
      success: false,
      requiresApproval: false,
      output: null,
      message: `No OpenSpec change found for issue #${issue.number}`,
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

    const reviewAcpOptions: AcpConnectionOptions = {
      ...acpOptions,
      executionId: `review-${issue.number}`,
      onSessionUpdate: (notification) => {
        if (!this.eventBus) return;
        try {
          this.eventBus.emit('plan_session_update', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'review',
            roundIndex: 0,
            sessionUpdate: notification.update.sessionUpdate,
            data: notification.update as unknown,
          });
        } catch {
          // fire-and-forget
        }
      },
    };

    let conn: AcpConnection | undefined;
    try {
      conn = await createAcpConnection(reviewAcpOptions);

      if (this.eventBus) {
        try {
          this.eventBus.emit('plan_round_start', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: this.projectId ?? issue.projectId,
            roundType: 'review',
            roundLabel: 'review',
            roundIndex: 0,
          });
        } catch {
          // fire-and-forget
        }
      }

      const reviewerPrompt = buildReviewerPrompt(issue, changeDir);
      const result = await conn.prompt(reviewerPrompt);

      await conn.close();

      return {
        success: result.success,
        requiresApproval: true,
        output: {
          stage: Stage.Review,
          issueNumber: issue.number,
          reviewReport: result.text,
        },
        message: result.success
          ? 'Review completed, awaiting user approval'
          : `Review failed: ${result.error ?? 'unknown error'}`,
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
