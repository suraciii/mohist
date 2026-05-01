import * as path from 'path';
import * as fs from 'fs';
import { Stage } from '../types';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../agents/artifact-prompt';
import { AcpRoundRunner, type RoundConfig } from './acp-round-runner';
import { cleanChangeDir, readReportFile } from './utils';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, StageRunResult } from './stage-context';
import type { CheckpointManager as AcpCheckpointManager } from './checkpoint-manager';

export class PlanStageRunner implements StageRunner {
  canHandle(stage: Stage): boolean {
    return stage === Stage.Plan || stage === Stage.Draft || stage === Stage.Backlog;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    const { issue, acpOptions, artifactManager, eventBus, checkpointManager } = ctx;

    const changeDir = artifactManager.getChangeDir(issue.number)
      || artifactManager.createChangeDir(issue.number, issue.title);
    if (!changeDir) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Failed to get or create change directory for issue #${issue.number}`,
      };
    }

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

    const resumeSteps = checkpointManager.getResumeSteps(issue.number, 'plan');
    if (resumeSteps.length === 0) {
      cleanChangeDir(changeDir);
    }

    const runner = new AcpRoundRunner({
      issue,
      changeDir,
      rounds,
      acpOptions,
      stage: 'plan',
      projectId: issue.projectId,
      eventBus,
      checkpointManager: checkpointManager as unknown as AcpCheckpointManager,
    });

    const result = await runner.execute();

    if (!result.success) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: result.message,
      };
    }

    checkpointManager.delete(issue.number, 'plan');

    const selfReviewReport = readReportFile(changeDir, 'self-review.md');

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
  }
}
