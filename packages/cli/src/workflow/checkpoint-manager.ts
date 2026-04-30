import type { PipelineCheckpointRepo, PipelineCheckpoint } from '../db/pipeline-checkpoint-repo';

export interface CheckpointManager {
  getResumeSteps(issueNumber: number, stage: string): string[];
  markStepComplete(issueNumber: number, stage: string, step: string, nextStep?: string | null): void;
  delete(issueNumber: number, stage: string): void;
  deleteAll(issueNumber: number): void;
}

export function createCheckpointManager(repo: PipelineCheckpointRepo): CheckpointManager {
  return new CheckpointManagerImpl(repo);
}

class CheckpointManagerImpl implements CheckpointManager {
  constructor(private repo: PipelineCheckpointRepo) {}

  getResumeSteps(issueNumber: number, stage: string): string[] {
    const checkpoint: PipelineCheckpoint | null = this.repo.get(issueNumber, stage);
    if (!checkpoint) {
      return [];
    }
    return [...checkpoint.completedSteps];
  }

  markStepComplete(issueNumber: number, stage: string, step: string, nextStep?: string | null): void {
    const existing: PipelineCheckpoint | null = this.repo.get(issueNumber, stage);
    const completedSteps: string[] = existing ? [...existing.completedSteps] : [];
    if (!completedSteps.includes(step)) {
      completedSteps.push(step);
    }
    this.repo.upsert(issueNumber, stage, completedSteps, nextStep ?? null);
  }

  delete(issueNumber: number, stage: string): void {
    this.repo.delete(issueNumber, stage);
  }

  deleteAll(issueNumber: number): void {
    this.repo.deleteAll(issueNumber);
  }
}