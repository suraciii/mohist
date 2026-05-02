import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage } from '../types';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../agents/artifact-prompt';
import { AcpRoundRunner, type RoundConfig } from './acp-round-runner';
import { cleanChangeDir, readReportFile } from './utils';
import { Log } from '../util/log';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, StageRunResult } from './stage-context';
import type { CheckpointManager as AcpCheckpointManager } from './checkpoint-manager';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'plan-stage' });

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

    // If already approved and artifacts exist, skip re-execution and advance to build
    if (issue.approvalState?.status === 'approved' && verifyPlanArtifacts(changeDir).length === 0) {
      return {
        success: true,
        requiresApproval: false,
        output: null,
        nextStage: Stage.Build,
        message: 'Plan already approved, advancing to build',
      };
    }

    const resumeSteps = checkpointManager.getResumeSteps(issue.number, 'plan');
    if (resumeSteps.length === 0) {
      cleanChangeDir(changeDir);
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

    const missingArtifacts = verifyPlanArtifacts(changeDir);
    if (missingArtifacts.length > 0) {
      log.error('Plan artifacts missing after all rounds completed', {
        changeDir,
        missing: missingArtifacts,
        issueNumber: issue.number,
      });
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Plan artifacts missing: ${missingArtifacts.join(', ')}`,
      };
    }

    const commitResult = await commitPlanArtifacts(changeDir, issue);
    if (!commitResult) {
      log.error('Failed to commit plan artifacts', {
        changeDir,
        issueNumber: issue.number,
      });
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Failed to commit plan artifacts for issue #${issue.number}`,
      };
    }

    checkpointManager.delete(issue.number, 'plan');

    const selfReviewReport = readReportFile(changeDir, 'self-review.md');

    return {
      success: true,
      requiresApproval: true,
      nextStage: Stage.Plan,
      output: {
        stage: Stage.Plan,
        issueNumber: issue.number,
        selfReviewNotes: selfReviewReport,
      },
      message: 'Plan completed, awaiting user approval',
    };
  }
}

const REQUIRED_PLAN_ARTIFACTS = ['proposal.md', 'design.md', 'tasks.json'];

function verifyPlanArtifacts(changeDir: string): string[] {
  return REQUIRED_PLAN_ARTIFACTS.filter(artifact => {
    const artifactPath = path.join(changeDir, artifact);
    if (!fs.existsSync(artifactPath)) return true;
    if (artifact.endsWith('.json')) {
      try {
        const content = fs.readFileSync(artifactPath, 'utf-8');
        const parsed = JSON.parse(content);
        return !parsed.tasks || !Array.isArray(parsed.tasks) || parsed.tasks.length === 0;
      } catch {
        return true;
      }
    }
    const stat = fs.statSync(artifactPath);
    return stat.size === 0;
  });
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
