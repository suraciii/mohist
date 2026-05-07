import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage } from '../types';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { readReportFile } from './utils';
import { Log } from '../util/log';
import { BaseStageRunner } from './base-stage-runner';
import type { StageContext, StageRunResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import type { Check } from './checks';
import { ProposalCompleteCheck } from './checks/proposal-complete-check';
import { SpecsCompleteCheck } from './checks/specs-complete-check';
import { DesignCompleteCheck } from './checks/design-complete-check';
import { TasksValidCheck } from './checks/tasks-valid-check';
import { SelfReviewPassedCheck } from './checks/self-review-passed-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { isCurrentStageApproval } from './issue-lifecycle';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'plan-stage' });

interface TaskConfig {
  type: string;
  label: string;
  outputPath: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: import('../types').Issue, changeDir: string) => string;
}

export class PlanStageRunner extends BaseStageRunner {
  canHandle(stage: Stage): boolean {
    return stage === Stage.Plan || stage === Stage.Draft || stage === Stage.Backlog;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const { issue, acpOptions, artifactManager, eventBus, checkpointManager } = ctx;

    const changeDir = artifactManager.getChangeDir(issue.number)
      || artifactManager.createChangeDir(issue.number, issue.title);
    if (!changeDir) {
      throw new Error(`Failed to get or create change directory for issue #${issue.number}`);
    }

    if (isCurrentStageApproval(issue, Stage.Plan, 'approved')) {
      checkpointManager.delete(issue.number, 'plan');
      return { changeDir, skipped: true };
    }

    const resumeSteps = checkpointManager.getResumeSteps(issue.number, 'plan');

    const tasks: TaskConfig[] = [
      {
        type: 'proposal',
        label: 'proposal.md',
        outputPath: path.join(changeDir, 'proposal.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'proposal.md')),
        buildPrompt: (iss, dir) => buildArtifactPrompt('proposal', iss, dir),
      },
      {
        type: 'specs',
        label: 'specs/',
        outputPath: path.join(changeDir, 'specs'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'specs')),
        buildPrompt: (iss, dir) => buildArtifactPrompt('specs', iss, dir),
      },
      {
        type: 'design',
        label: 'design.md',
        outputPath: path.join(changeDir, 'design.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'design.md')),
        buildPrompt: (iss, dir) => buildArtifactPrompt('design', iss, dir),
      },
      {
        type: 'tasks',
        label: 'tasks.json',
        outputPath: path.join(changeDir, 'tasks.json'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'tasks.json')),
        buildPrompt: (iss, dir) => buildArtifactPrompt('tasks', iss, dir),
      },
      {
        type: 'self-review',
        label: 'self-review.md',
        outputPath: path.join(changeDir, 'self-review.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'self-review.md')),
        buildPrompt: (iss, dir) => buildSelfReviewPrompt(iss, dir),
      },
    ];

    const completedSteps = [...resumeSteps];

    const connectionOptions: AgentSessionOptions = {
      ...acpOptions,
      issueId: issue.id,
      projectId: issue.projectId,
      issueNumber: issue.number,
      executionId: `plan-${issue.number}`,
      stage: 'plan',
      title: 'Plan stage',
      onSessionUpdate: (_notification) => {
        if (!eventBus) return;
        try {
          eventBus.emit('plan_session_update', {
            issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
            projectId: issue.projectId,
            roundType: '',
            roundIndex: 0,
            sessionUpdate: _notification.update.sessionUpdate,
            data: _notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', {
            error: e instanceof Error ? e.message : String(e),
          });
        }
      },
    };

    let session: AgentSession | undefined;

    try {
      session = await AgentSession.create(connectionOptions);

      for (const [index, task] of tasks.entries()) {
        if (completedSteps.includes(task.type)) {
          if (task.verifyArtifact()) {
            log.info('Plan task skipped (checkpoint + artifact exists)', {
              artifact: task.type,
              issueNumber: issue.number,
            });
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'skipped',
              artifacts: [],
              attempts: 0,
              duration: 0,
            });
            continue;
          }
          log.info('Plan task in checkpoint but artifact missing, re-running', {
            artifact: task.type,
            issueNumber: issue.number,
          });
          const idx = completedSteps.indexOf(task.type);
          completedSteps.splice(idx);
        } else if (task.verifyArtifact()) {
          log.info('Plan artifact exists but not in checkpoint, marking complete', {
            artifact: task.type,
            issueNumber: issue.number,
          });
          completedSteps.push(task.type);
          checkpointManager.markStepComplete(
            issue.number,
            'plan',
            task.type,
            tasks[index + 1]?.type ?? null,
          );
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'skipped',
            artifacts: [task.outputPath],
            attempts: 0,
            duration: 0,
          });
          continue;
        }

        log.info('Plan task', { artifact: task.type, issueNumber: issue.number });

        emitRoundStart(eventBus, task.type, index, acpOptions, issue.projectId ?? '');
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'started', 1, []);

        const taskStartTime = Date.now();
        let attempts = 1;

        const prompt = task.buildPrompt(issue, changeDir);
        const result = await session.execute(prompt, { kind: 'task', title: task.label });

        if (!result.success) {
          log.error('Plan task failed', { artifact: task.type, error: result.error });
          emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'failed',
            artifacts: [],
            attempts,
            duration: Date.now() - taskStartTime,
          });
          await session.close();
          throw new Error(`Task "${task.label}" failed: ${result.error ?? 'unknown error'}`);
        }

        if (!task.verifyArtifact()) {
          log.warn('Plan artifact not found after task, sending retry', {
            artifact: task.label,
            taskIndex: index,
          });

          const retryPrompt = [
            `The artifact file ${task.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${task.type} artifact to:`,
            task.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('Plan retry prompt sent', { artifact: task.type, taskIndex: index });
          attempts++;
          emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'retrying', attempts, []);

          const retryResult = await session.execute(retryPrompt, { kind: 'retry', title: task.label });

          if (!retryResult.success) {
            log.error('Plan retry prompt failed', { artifact: task.type, error: retryResult.error });
            emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'failed',
              artifacts: [],
              attempts,
              duration: Date.now() - taskStartTime,
            });
            await session.close();
            throw new Error(`Task "${task.label}" retry failed: ${retryResult.error ?? 'unknown error'}`);
          }

          if (!task.verifyArtifact()) {
            log.error('Plan artifact still missing after retry', { artifact: task.label });
            emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'failed',
              artifacts: [],
              attempts,
              duration: Date.now() - taskStartTime,
            });
            await session.close();
            throw new Error(`Artifact "${task.label}" not found after retry`);
          }

          log.info('Plan retry succeeded', { artifact: task.label });
        }

        const taskDuration = Date.now() - taskStartTime;
        const taskArtifacts = task.verifyArtifact() ? [task.label] : [];
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'completed', attempts, taskArtifacts);
        this.appendTaskResult(ctx, {
          taskId: task.type,
          title: task.label,
          status: 'completed',
          artifacts: taskArtifacts,
          attempts,
          duration: taskDuration,
        });

        completedSteps.push(task.type);
        checkpointManager.markStepComplete(
          issue.number,
          'plan',
          task.type,
          tasks[index + 1]?.type ?? null,
        );
      }

      await session.close();
    } catch (err) {
      if (session) {
        try {
          await session.close();
        } catch {
          // ignore cleanup errors
        }
      }
      throw err;
    }

    const commitOk = await commitPlanArtifacts(changeDir, issue);
    if (!commitOk) {
      throw new Error(`Failed to commit plan artifacts for issue #${issue.number}`);
    }

    checkpointManager.delete(issue.number, 'plan');

    return { changeDir };
  }

  protected getChecks(): Check[] {
    return [
      new ProposalCompleteCheck(),
      new SpecsCompleteCheck(),
      new DesignCompleteCheck(),
      new TasksValidCheck(),
      new SelfReviewPassedCheck(),
      new UserApprovalCheck(Stage.Plan),
    ];
  }

  protected getNextStage(): Stage {
    return Stage.Build;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    const result = await super.run(ctx);

    if (result.success) {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      const selfReviewReport = changeDir ? readReportFile(changeDir, 'self-review.md') : null;
      return {
        ...result,
        output: {
          stage: Stage.Plan,
          issueNumber: ctx.issue.number,
          selfReviewNotes: selfReviewReport,
        },
      };
    }

    return result;
  }
}

function emitRoundStart(
  eventBus: import('../services/event-bus').EventBus | undefined,
  roundType: string,
  roundIndex: number,
  acpOptions: AgentSessionOptions,
  projectId: string,
): void {
  if (!eventBus) return;
  try {
    eventBus.emit('plan_round_start', {
      issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
      projectId,
      roundType,
      roundLabel: roundType,
      roundIndex,
    });
  } catch (e) {
    log.warn('eventBus.emit failed for plan_round_start', {
      roundType,
      error: e instanceof Error ? e.message : String(e),
    });
  }
}

async function commitPlanArtifacts(changeDir: string, issue: { number: number; title: string }): Promise<boolean> {
  try {
    const worktreePath = path.dirname(path.dirname(path.dirname(changeDir)));
    const relPath = path.relative(worktreePath, changeDir);

    const { stdout: statusOut } = await execFileAsync(
      'git',
      ['status', '--porcelain', '--', relPath],
      { cwd: worktreePath },
    );

    if (!statusOut.trim()) {
      log.info('No uncommitted plan artifacts', { issueNumber: issue.number });
      return true;
    }

    await execFileAsync('git', ['add', '--', relPath], { cwd: worktreePath });
    await execFileAsync(
      'git',
      ['commit', '-m', `plan(issue-${issue.number}): ${issue.title}`, '--no-verify'],
      { cwd: worktreePath },
    );

    log.info('Plan artifacts committed', { issueNumber: issue.number, changeDir });
    return true;
  } catch (err) {
    log.warn('Failed to commit plan artifacts', {
      issueNumber: issue.number,
      error: err instanceof Error ? err.message : String(err),
    });
    return false;
  }
}
