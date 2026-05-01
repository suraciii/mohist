import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, type SqlValue } from './database';

export type TaskType = 'start-pipeline' | 'resume-pipeline' | 'rebase';
export type TaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'cancelled';

export interface IssueTaskQueueRow {
  id: string;
  issue_id: string;
  issue_number: number;
  project_id: string;
  task_type: string;
  payload: string;
  priority: number;
  status: string;
  enqueued_at: string;
  started_at: string | null;
  result: string | null;
  completed_at: string | null;
}

export interface IssueTaskQueueRecord {
  id: string;
  issueId: string;
  issueNumber: number;
  projectId: string;
  taskType: TaskType;
  payload: string;
  priority: number;
  status: TaskStatus;
  enqueuedAt: string;
  startedAt: string | null;
  result: string | null;
  completedAt: string | null;
}

function rowToRecord(row: IssueTaskQueueRow): IssueTaskQueueRecord {
  return {
    id: row.id,
    issueId: row.issue_id,
    issueNumber: row.issue_number,
    projectId: row.project_id,
    taskType: row.task_type as TaskType,
    payload: row.payload,
    priority: row.priority,
    status: row.status as TaskStatus,
    enqueuedAt: row.enqueued_at,
    startedAt: row.started_at,
    result: row.result,
    completedAt: row.completed_at,
  };
}

export interface CreateTaskQueueEntryData {
  issueId: string;
  issueNumber: number;
  projectId: string;
  taskType: TaskType;
  payload?: string;
  priority?: number;
}

export interface UpdateTaskStatusFields {
  startedAt?: string;
  result?: string;
  completedAt?: string;
}

export class IssueTaskQueueRepo {
  constructor(private db: DatabaseManager) {}

  insert(data: CreateTaskQueueEntryData): IssueTaskQueueRecord {
    const id = uuidv4();
    const now = new Date().toISOString();
    const payload = data.payload ?? '{}';
    const priority = data.priority ?? 0;

    this.db.run(
      `INSERT INTO issue_task_queue (id, issue_id, issue_number, project_id, task_type, payload, priority, status, enqueued_at, started_at, result, completed_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, 'pending', ?, NULL, NULL, NULL)`,
      [id, data.issueId, data.issueNumber, data.projectId, data.taskType, payload, priority, now]
    );

    return {
      id,
      issueId: data.issueId,
      issueNumber: data.issueNumber,
      projectId: data.projectId,
      taskType: data.taskType,
      payload,
      priority,
      status: 'pending',
      enqueuedAt: now,
      startedAt: null,
      result: null,
      completedAt: null,
    };
  }

  updateStatus(id: string, status: TaskStatus, fields?: UpdateTaskStatusFields): boolean {
    const sets: string[] = ['status = ?'];
    const params: SqlValue[] = [status];

    if (fields?.startedAt !== undefined) {
      sets.push('started_at = ?');
      params.push(fields.startedAt);
    }
    if (fields?.result !== undefined) {
      sets.push('result = ?');
      params.push(fields.result);
    }
    if (fields?.completedAt !== undefined) {
      sets.push('completed_at = ?');
      params.push(fields.completedAt);
    }

    params.push(id);
    const result = this.db.run(
      `UPDATE issue_task_queue SET ${sets.join(', ')} WHERE id = ?`,
      params
    );
    return result.changes > 0;
  }

  findById(id: string): IssueTaskQueueRecord | null {
    const row = this.db.get<IssueTaskQueueRow>(
      'SELECT * FROM issue_task_queue WHERE id = ?',
      [id]
    );
    return row ? rowToRecord(row) : null;
  }

  findByIssueId(issueId: string): IssueTaskQueueRecord[] {
    const rows = this.db.all<IssueTaskQueueRow>(
      'SELECT * FROM issue_task_queue WHERE issue_id = ? ORDER BY priority DESC, enqueued_at ASC',
      [issueId]
    );
    return rows.map(rowToRecord);
  }

  findPendingByIssueId(issueId: string): IssueTaskQueueRecord[] {
    const rows = this.db.all<IssueTaskQueueRow>(
      "SELECT * FROM issue_task_queue WHERE issue_id = ? AND status = 'pending' ORDER BY priority DESC, enqueued_at ASC",
      [issueId]
    );
    return rows.map(rowToRecord);
  }

  findRunningByIssueId(issueId: string): IssueTaskQueueRecord | null {
    const row = this.db.get<IssueTaskQueueRow>(
      "SELECT * FROM issue_task_queue WHERE issue_id = ? AND status = 'running'",
      [issueId]
    );
    return row ? rowToRecord(row) : null;
  }

  findAllPending(): IssueTaskQueueRecord[] {
    const rows = this.db.all<IssueTaskQueueRow>(
      "SELECT * FROM issue_task_queue WHERE status = 'pending' ORDER BY priority DESC, enqueued_at ASC"
    );
    return rows.map(rowToRecord);
  }

  findAllRunning(): IssueTaskQueueRecord[] {
    const rows = this.db.all<IssueTaskQueueRow>(
      "SELECT * FROM issue_task_queue WHERE status = 'running'"
    );
    return rows.map(rowToRecord);
  }

  deleteByIssueId(issueId: string): number {
    const result = this.db.run(
      'DELETE FROM issue_task_queue WHERE issue_id = ?',
      [issueId]
    );
    return result.changes;
  }

  cancelPendingByIssueId(issueId: string): number {
    const now = new Date().toISOString();
    const result = this.db.run(
      "UPDATE issue_task_queue SET status = 'cancelled', result = 'cancelled', completed_at = ? WHERE issue_id = ? AND status = 'pending'",
      [now, issueId]
    );
    return result.changes;
  }
}
