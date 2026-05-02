import { Stage } from '../types';
import type { StageContext, StageRunResult } from './stage-context';
import type { StageRunner } from './check-stage-runner';
import { BaseStageRunner } from './base-stage-runner';
import type { Check } from './checks';
import { BuildTestCheck } from './checks/build-test-check';
import { AiReviewCheck } from './checks/ai-review-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { AcpRoundRunner, type AcpRoundRunnerOptions, type RoundConfig } from './acp-round-runner';
import { buildReviewerPrompt, buildReviewSelfCheckPrompt } from '../agents/artifact-prompt';
import { readReportFile } from './utils';
import { Log } from '../util/log';

const log = Log.create({ service: 'check-stage-runner' });

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export interface CheckStageRunnerOptions {
  worktreePath: string;
}

export class CheckStageRunner extends BaseStageRunner implements StageRunner {
  private worktreePath: string;
  private checks: Check[];

  constructor(options: CheckStageRunnerOptions) {
    super();
    this.worktreePath = options.worktreePath;
    this.checks = [
      new BuildTestCheck({ worktreePath: this.worktreePath }),
      new AiReviewCheck(),
      new UserApprovalCheck(Stage.Plan),
    ];
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Check;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
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

    const runnerOptions: AcpRoundRunnerOptions = {
      issue: ctx.issue,
      changeDir,
      rounds,
      acpOptions: {
        ...ctx.acpOptions,
        executionId: `review-${ctx.issue.number}`,
      },
      stage: 'review',
      projectId: ctx.issue.projectId,
      eventBus: ctx.eventBus,
      checkpointManager: ctx.checkpointManager,
    };

    const runner = new AcpRoundRunner(runnerOptions);
    const result = await runner.execute();

    if (!result.success) {
      throw new Error(result.message ?? 'Review round execution failed');
    }

    return result;
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
