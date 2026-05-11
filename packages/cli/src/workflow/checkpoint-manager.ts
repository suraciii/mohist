import type { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { Log } from '../util/log';

const log = Log.create({ service: 'checkpoint-manager' });

export class CheckpointManager {
  constructor(private repo: PipelineCheckpointRepo) {}

  getResumeSteps(issueNumber: number, stage: string): string[] {
    const checkpoint = this.repo.get(issueNumber, stage);
    if (!checkpoint) return [];
    if (checkpoint.completedSteps.length > 0) {
      log.info('Resuming from checkpoint', {
        issueNumber,
        stage,
        completedSteps: checkpoint.completedSteps,
        nextStep: checkpoint.nextStep,
      });
    }
    return [...checkpoint.completedSteps];
  }

  markStepComplete(issueNumber: number, stage: string, step: string, nextStep?: string | null): void {
    const current = this.getResumeSteps(issueNumber, stage);
    if (current.includes(step)) return;
    current.push(step);
    this.repo.upsert(issueNumber, stage, current, nextStep ?? null);
  }

  delete(issueNumber: number, stage: string): void {
    this.repo.delete(issueNumber, stage);
  }

  deleteStep(issueNumber: number, stage: string, step: string): void {
    const current = this.getResumeSteps(issueNumber, stage);
    const idx = current.indexOf(step);
    if (idx >= 0) {
      current.splice(idx, 1);
      this.repo.upsert(issueNumber, stage, current, null);
    }
  }

  deleteAll(issueNumber: number): void {
    this.repo.deleteAll(issueNumber);
  }

  hasStep(issueNumber: number, stage: string, step: string): boolean {
    return this.getResumeSteps(issueNumber, stage).includes(step);
  }

  upsert(issueNumber: number, stage: string, completedSteps: string[], nextStep: string | null): void {
    this.repo.upsert(issueNumber, stage, completedSteps, nextStep);
  }
}

export function createCheckpointManager(repo: PipelineCheckpointRepo): CheckpointManager {
  return new CheckpointManager(repo);
}
