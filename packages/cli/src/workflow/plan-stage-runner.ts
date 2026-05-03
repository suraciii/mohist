import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage } from '../types';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../agents/artifact-prompt';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { readReportFile } from './utils';
import { Log } from '../util/log';
import { BaseStageRunner } from './base-stage-runner';
import type { StageContext, StageRunResult } from './stage-context';
import type { Check } from './checks';
import { ProposalCompleteCheck } from './checks/proposal-complete-check';
import { SpecsCompleteCheck } from './checks/specs-complete-check';
import { DesignCompleteCheck } from './checks/design-complete-check';
import { TasksValidCheck } from './checks/tasks-valid-check';
import { SelfReviewPassedCheck } from './checks/self-review-passed-check';
import { UserApprovalCheck } from './checks/user-approval-check';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'plan-stage' });

interface RoundConfig {
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

    if (issue.approvalState?.status === 'approved') {
      checkpointManager.delete(issue.number, 'plan');
      return { changeDir, skipped: true };
    }

    const resumeSteps = checkpointManager.getResumeSteps(issue.number, 'plan');

    const rounds: RoundConfig[] = [
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

    const connectionOptions: AcpConnectionOptions = {
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

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(connectionOptions);

      for (const [index, round] of rounds.entries()) {
        if (completedSteps.includes(round.type)) {
          if (round.verifyArtifact()) {
            log.info('Plan round skipped (checkpoint + artifact exists)', {
              artifact: round.type,
              issueNumber: issue.number,
            });
            continue;
          }
          log.info('Plan round in checkpoint but artifact missing, re-running', {
            artifact: round.type,
            issueNumber: issue.number,
          });
          const idx = completedSteps.indexOf(round.type);
          completedSteps.splice(idx);
        } else if (round.verifyArtifact()) {
          log.info('Plan artifact exists but not in checkpoint, marking complete', {
            artifact: round.type,
            issueNumber: issue.number,
          });
          completedSteps.push(round.type);
          checkpointManager.markStepComplete(
            issue.number,
            'plan',
            round.type,
            rounds[index + 1]?.type ?? null,
          );
          continue;
        }

        log.info('Plan round', { artifact: round.type, issueNumber: issue.number });

        emitRoundStart(eventBus, round.type, index, acpOptions, issue.projectId ?? '');

        const prompt = round.buildPrompt(issue, changeDir);
        const result = await conn.prompt(prompt);

        if (!result.success) {
          log.error('Plan round failed', { artifact: round.type, error: result.error });
          await conn.close();
          throw new Error(`Round "${round.label}" failed: ${result.error ?? 'unknown error'}`);
        }

        if (!round.verifyArtifact()) {
          log.warn('Plan artifact not found after round, sending retry', {
            artifact: round.label,
            roundIndex: index,
          });

          const retryPrompt = [
            `The artifact file ${round.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${round.type} artifact to:`,
            round.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('Plan retry prompt sent', { artifact: round.type, roundIndex: index });

          const retryResult = await conn.prompt(retryPrompt);

          if (!retryResult.success) {
            log.error('Plan retry prompt failed', { artifact: round.type, error: retryResult.error });
            await conn.close();
            throw new Error(`Round "${round.label}" retry failed: ${retryResult.error ?? 'unknown error'}`);
          }

          if (!round.verifyArtifact()) {
            log.error('Plan artifact still missing after retry', { artifact: round.label });
            await conn.close();
            throw new Error(`Artifact "${round.label}" not found after retry`);
          }

          log.info('Plan retry succeeded', { artifact: round.label });
        }

        completedSteps.push(round.type);
        checkpointManager.markStepComplete(
          issue.number,
          'plan',
          round.type,
          rounds[index + 1]?.type ?? null,
        );
      }

      await conn.close();
    } catch (err) {
      if (conn) {
        try {
          await conn.close();
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
  acpOptions: AcpConnectionOptions,
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
