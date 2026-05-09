import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { Stage } from '../types';
import type { StageTaskResult } from '../workflow/stage-context';

export type StageExecutionStatus = 'running' | 'awaiting-approval' | 'passed' | 'failed';

export interface StageExecution {
  id: string;
  issueId: string;
  stage: Stage;
  status: StageExecutionStatus;
  taskResults: StageTaskResult[];
  checkResults: unknown[];
  createdAt: string;
  updatedAt: string;
}

interface StageExecutionRow {
  id: string;
  issue_id: string;
  stage: string;
  status: string;
  task_results: string;
  check_results: string;
  created_at: string;
  updated_at: string;
}

function rowToStageExecution(row: StageExecutionRow): StageExecution {
  return {
    id: row.id,
    issueId: row.issue_id,
    stage: row.stage as Stage,
    status: row.status as StageExecutionStatus,
    taskResults: JSON.parse(row.task_results),
    checkResults: JSON.parse(row.check_results),
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export class StageExecutionRepo {
  constructor(private db: DatabaseManager) {}

  create(issueId: string, stage: Stage): StageExecution {
    const now = new Date().toISOString();
    const id = uuidv4();

    this.db.run(
      `INSERT INTO stage_executions (id, issue_id, stage, status, task_results, check_results, created_at, updated_at)
       VALUES (?, ?, ?, 'running', '[]', '[]', ?, ?)`,
      [id, issueId, stage, now, now]
    );

    return {
      id,
      issueId,
      stage,
      status: 'running',
      taskResults: [],
      checkResults: [],
      createdAt: now,
      updatedAt: now,
    };
  }

  updateCheckResults(id: string, checkResults: unknown[]): StageExecution | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE stage_executions SET check_results = ?, updated_at = ? WHERE id = ?',
      [JSON.stringify(checkResults), now, id]
    );
    return this.findById(id);
  }

  updateTaskResults(id: string, taskResults: StageTaskResult[]): StageExecution | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE stage_executions SET task_results = ?, updated_at = ? WHERE id = ?',
      [JSON.stringify(taskResults), now, id]
    );
    return this.findById(id);
  }

  appendTaskResult(id: string, result: StageTaskResult): StageExecution | null {
    const current = this.findById(id);
    if (!current) return null;
    const updated = [...current.taskResults, result];
    return this.updateTaskResults(id, updated);
  }

  updateStatus(id: string, status: StageExecutionStatus): StageExecution | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE stage_executions SET status = ?, updated_at = ? WHERE id = ?',
      [status, now, id]
    );
    return this.findById(id);
  }

  closeActiveByIssueId(issueId: string, status: StageExecutionStatus = 'failed'): number {
    const now = new Date().toISOString();
    const result = this.db.run(
      `UPDATE stage_executions
       SET status = ?, updated_at = ?
       WHERE issue_id = ? AND status IN ('running', 'awaiting-approval')`,
      [status, now, issueId]
    ) as unknown as { changes?: number };
    return result.changes ?? 0;
  }

  findActiveByIssueId(issueId: string): StageExecution | null {
    const row = this.db.get<StageExecutionRow>(
      `SELECT * FROM stage_executions
       WHERE issue_id = ? AND status IN ('running', 'awaiting-approval')
       ORDER BY created_at DESC LIMIT 1`,
      [issueId]
    );
    return row ? rowToStageExecution(row) : null;
  }

  findById(id: string): StageExecution | null {
    const row = this.db.get<StageExecutionRow>(
      'SELECT * FROM stage_executions WHERE id = ?',
      [id]
    );
    return row ? rowToStageExecution(row) : null;
  }

  findByIssueId(issueId: string): StageExecution[] {
    const rows = this.db.all<StageExecutionRow>(
      `SELECT * FROM stage_executions
       WHERE issue_id = ?
       ORDER BY created_at ASC`,
      [issueId]
    );
    return rows.map(rowToStageExecution);
  }
}
