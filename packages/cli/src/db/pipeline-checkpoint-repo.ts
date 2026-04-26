import { DatabaseManager } from './database';

export interface PipelineCheckpoint {
  issueNumber: number;
  stage: string;
  completedSteps: string[];
  nextStep: string | null;
  updatedAt: string;
}

interface PipelineCheckpointRow {
  issue_number: number;
  stage: string;
  completed_steps: string;
  next_step: string | null;
  updated_at: string;
}

function rowToCheckpoint(row: PipelineCheckpointRow): PipelineCheckpoint {
  return {
    issueNumber: row.issue_number,
    stage: row.stage,
    completedSteps: JSON.parse(row.completed_steps),
    nextStep: row.next_step,
    updatedAt: row.updated_at,
  };
}

export class PipelineCheckpointRepo {
  constructor(private db: DatabaseManager) {}

  get(issueNumber: number, stage: string): PipelineCheckpoint | null {
    const row = this.db.get<PipelineCheckpointRow>(
      'SELECT * FROM pipeline_checkpoint WHERE issue_number = ? AND stage = ?',
      [issueNumber, stage]
    );
    return row ? rowToCheckpoint(row) : null;
  }

  upsert(issueNumber: number, stage: string, completedSteps: string[], nextStep: string | null): void {
    const stepsJson = JSON.stringify(completedSteps);
    this.db.run(
      `INSERT INTO pipeline_checkpoint (issue_number, stage, completed_steps, next_step, updated_at)
       VALUES (?, ?, ?, ?, datetime('now'))
       ON CONFLICT(issue_number, stage) DO UPDATE SET
         completed_steps = excluded.completed_steps,
         next_step = excluded.next_step,
         updated_at = excluded.updated_at`,
      [issueNumber, stage, stepsJson, nextStep]
    );
  }

  delete(issueNumber: number, stage: string): void {
    this.db.run(
      'DELETE FROM pipeline_checkpoint WHERE issue_number = ? AND stage = ?',
      [issueNumber, stage]
    );
  }

  deleteAll(issueNumber: number): void {
    this.db.run(
      'DELETE FROM pipeline_checkpoint WHERE issue_number = ?',
      [issueNumber]
    );
  }
}
