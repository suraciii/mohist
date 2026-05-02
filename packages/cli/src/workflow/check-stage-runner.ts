import { Stage } from '../types';
import type { StageContext, StageRunResult } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import type { Check } from './checks';
import { BuildTestCheck } from './checks/build-test-check';
import { AiReviewCheck } from './checks/ai-review-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { buildReviewerPrompt, buildReviewSelfCheckPrompt } from '../agents/artifact-prompt';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { readReportFile } from './utils';
import { Log } from '../util/log';

const log = Log.create({ service: 'check-stage-runner' });

interface RoundConfig {
  type: string;
  label: string;
  outputPath: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: import('../types').Issue, changeDir: string) => string;
}

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export interface CheckStageRunnerOptions {
  worktreePath: string;
  checks?: Check[];
}

export class CheckStageRunner extends BaseStageRunner implements StageRunner {
  private worktreePath: string;
  private checks: Check[];
  private usesDefaultChecks: boolean;

  constructor(options: CheckStageRunnerOptions) {
    super();
    this.worktreePath = options.worktreePath;
    this.usesDefaultChecks = !options.checks;
    this.checks = options.checks ?? [
      new BuildTestCheck({ worktreePath: this.worktreePath }),
      new AiReviewCheck(),
      new UserApprovalCheck(Stage.Plan),
    ];
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Check;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    if (!this.usesDefaultChecks) {
      return { done: true };
    }

    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) {
      throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
    }

    const reviewOutputPath = 'review.md';
    const selfCheckOutputPath = 'review-self-check.md';

    const rounds: RoundConfig[] = [
      {
        type: 'review',
        label: 'review',
        outputPath: changeDir + '/' + reviewOutputPath,
        verifyArtifact: () => readReportFile(changeDir, reviewOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewerPrompt(issue, dir),
      },
      {
        type: 'review-self-check',
        label: 'review-self-check',
        outputPath: changeDir + '/' + selfCheckOutputPath,
        verifyArtifact: () => readReportFile(changeDir, selfCheckOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewSelfCheckPrompt(issue, dir),
      },
    ];

    const resumeSteps = ctx.checkpointManager
      ? ctx.checkpointManager.getResumeSteps(ctx.issue.number, 'review')
      : [];
    const completedSteps = [...resumeSteps];

    const roundState = { type: '', index: 0 };

    const connectionOptions: AcpConnectionOptions = {
      ...ctx.acpOptions,
      executionId: `review-${ctx.issue.number}`,
      stage: 'review',
      onSessionUpdate: (_notification) => {
        if (!ctx.eventBus) return;
        try {
          ctx.eventBus.emit('plan_session_update', {
            issueId: String(ctx.acpOptions.issueNumber ?? ctx.acpOptions.issueId ?? ''),
            projectId: ctx.issue.projectId,
            roundType: roundState.type,
            roundIndex: roundState.index,
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
        roundState.type = round.type;
        roundState.index = index;

        if (completedSteps.includes(round.type)) {
          if (round.verifyArtifact()) {
            log.info('Review round skipped (checkpoint + artifact exists)', {
              artifact: round.type,
              issueNumber: ctx.issue.number,
            });
            continue;
          }
          log.info('Review round in checkpoint but artifact missing, re-running', {
            artifact: round.type,
            issueNumber: ctx.issue.number,
          });
          const idx = completedSteps.indexOf(round.type);
          completedSteps.splice(idx);
        } else if (round.verifyArtifact()) {
          log.info('Review artifact exists but not in checkpoint, marking complete', {
            artifact: round.type,
            issueNumber: ctx.issue.number,
          });
          completedSteps.push(round.type);
          ctx.checkpointManager?.markStepComplete(
            ctx.issue.number,
            'review',
            round.type,
            rounds[index + 1]?.type ?? null,
          );
          continue;
        }

        log.info('Review round', { artifact: round.type, issueNumber: ctx.issue.number });

        emitReviewRoundStart(ctx.eventBus, round.type, index, ctx.acpOptions, ctx.issue.projectId ?? '');

        const prompt = round.buildPrompt(ctx.issue, changeDir);
        const result = await conn.prompt(prompt);

        if (!result.success) {
          log.error('Review round failed', { artifact: round.type, error: result.error });
          await conn.close();
          throw new Error(`Round "${round.label}" failed: ${result.error ?? 'unknown error'}`);
        }

        if (!round.verifyArtifact()) {
          log.warn('Review artifact not found after round, sending retry', {
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

          log.info('Review retry prompt sent', { artifact: round.type, roundIndex: index });

          const retryResult = await conn.prompt(retryPrompt);

          if (!retryResult.success) {
            log.error('Review retry prompt failed', { artifact: round.type, error: retryResult.error });
            await conn.close();
            throw new Error(`Round "${round.label}" retry failed: ${retryResult.error ?? 'unknown error'}`);
          }

          if (!round.verifyArtifact()) {
            log.error('Review artifact still missing after retry', { artifact: round.label });
            await conn.close();
            throw new Error(`Artifact "${round.label}" not found after retry`);
          }

          log.info('Review retry succeeded', { artifact: round.label });
        }

        completedSteps.push(round.type);
        ctx.checkpointManager?.markStepComplete(
          ctx.issue.number,
          'review',
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

    return { success: true };
  }

  protected getChecks(): Check[] {
    return this.checks;
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    const result = await super.run(ctx);

    if (result.success) {
      try {
        await ctx.artifactManager.archiveChange(ctx.issue.number);
      } catch (err) {
        log.error('Failed to archive change', {
          issueNumber: ctx.issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    return result;
  }
}

function emitReviewRoundStart(
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
