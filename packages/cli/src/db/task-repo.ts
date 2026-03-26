import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, SqlValue } from './database';
import { Task, Stage } from '../types';

interface TaskRow {
  id: string;
  issue_id: string;
  project_id: string;
  stage: string;
  status: string;
  agent_pid: number | null;
  error: string | null;
  started_at: string | null;
  completed_at: string | null;
}

type TaskStatus = 'pending' | 'running' | 'completed' | 'failed';

function rowToTask(row: TaskRow): Task {
  return {
    id: row.id,
    issueId: row.issue_id,
    projectId: row.project_id,
    stage: row.stage as Stage,
    status: row.status as TaskStatus,
    agentPid: row.agent_pid || undefined,
    startedAt: row.started_at || undefined,
    completedAt: row.completed_at || undefined,
    error: row.error || undefined,
  };
}

export interface CreateTaskData {
  issueId: string;
  projectId: string;
  stage: Stage;
}

export interface TaskQueryOptions {
  projectId?: string;
  issueId?: string;
  status?: TaskStatus;
}

export class TaskRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateTaskData): Task {
    const now = new Date().toISOString();
    const id = uuidv4();
    
    this.db.run(
      `INSERT INTO tasks (id, issue_id, project_id, stage, status, started_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [id, data.issueId, data.projectId, data.stage, 'pending', now]
    );
    
    return {
      id,
      issueId: data.issueId,
      projectId: data.projectId,
      stage: data.stage,
      status: 'pending',
      startedAt: now,
    };
  }

  findById(id: string): Task | null {
    const row = this.db.get<TaskRow>(
      'SELECT * FROM tasks WHERE id = ?',
      [id]
    );
    return row ? rowToTask(row) : null;
  }

  findByIssueId(issueId: string): Task[] {
    const rows = this.db.all<TaskRow>(
      'SELECT * FROM tasks WHERE issue_id = ? ORDER BY started_at DESC',
      [issueId]
    );
    return rows.map(rowToTask);
  }

  findAll(options: TaskQueryOptions = {}): Task[] {
    let sql = 'SELECT * FROM tasks WHERE 1=1';
    const params: SqlValue[] = [];
    
    if (options.projectId) {
      sql += ' AND project_id = ?';
      params.push(options.projectId);
    }
    if (options.issueId) {
      sql += ' AND issue_id = ?';
      params.push(options.issueId);
    }
    if (options.status) {
      sql += ' AND status = ?';
      params.push(options.status);
    }
    
    sql += ' ORDER BY started_at DESC';
    
    const rows = this.db.all<TaskRow>(sql, params);
    return rows.map(rowToTask);
  }

  findRunning(): Task[] {
    return this.findAll({ status: 'running' });
  }

  findPending(): Task[] {
    return this.findAll({ status: 'pending' });
  }

  findAndClaim(): Task | null {
    const now = new Date().toISOString();
    const row = this.db.get<TaskRow>(
      `UPDATE tasks SET status = 'running', started_at = ?
       WHERE id = (
         SELECT id FROM tasks t
         WHERE t.status = 'pending'
         AND NOT EXISTS (
           SELECT 1 FROM tasks t2
           WHERE t2.issue_id = t.issue_id AND t2.status = 'running'
         )
         ORDER BY t.started_at ASC
         LIMIT 1
       )
       RETURNING *`,
      [now]
    );
    return row ? rowToTask(row) : null;
  }

  findRunningByIssue(issueId: string): Task | null {
    const row = this.db.get<TaskRow>(
      'SELECT * FROM tasks WHERE issue_id = ? AND status = ?',
      [issueId, 'running']
    );
    return row ? rowToTask(row) : null;
  }

  findRunningByProject(projectId: string): Task[] {
    const rows = this.db.all<TaskRow>(
      'SELECT * FROM tasks WHERE project_id = ? AND status = ?',
      [projectId, 'running']
    );
    return rows.map(rowToTask);
  }

  updateStatus(taskId: string, status: TaskStatus, error?: string): Task | null {
    const now = new Date().toISOString();
    
    if (status === 'completed' || status === 'failed') {
      this.db.run(
        'UPDATE tasks SET status = ?, error = ?, completed_at = ? WHERE id = ?',
        [status, error || null, now, taskId]
      );
    } else {
      this.db.run(
        'UPDATE tasks SET status = ?, error = ? WHERE id = ?',
        [status, error || null, taskId]
      );
    }
    
    return this.findById(taskId);
  }

  setAgentPid(taskId: string, pid: number): void {
    this.db.run(
      'UPDATE tasks SET agent_pid = ?, status = ? WHERE id = ?',
      [pid, 'running', taskId]
    );
  }

  clearAgentPid(taskId: string): void {
    this.db.run(
      'UPDATE tasks SET agent_pid = NULL WHERE id = ?',
      [taskId]
    );
  }

  delete(taskId: string): boolean {
    const result = this.db.run('DELETE FROM tasks WHERE id = ?', [taskId]);
    return result.changes > 0;
  }

  deleteByIssue(issueId: string): number {
    const result = this.db.run('DELETE FROM tasks WHERE issue_id = ?', [issueId]);
    return result.changes;
  }

  deleteByProject(projectId: string): number {
    const result = this.db.run('DELETE FROM tasks WHERE project_id = ?', [projectId]);
    return result.changes;
  }

  countRunning(): number {
    const row = this.db.get<{ count: number }>(
      'SELECT COUNT(*) as count FROM tasks WHERE status = ?',
      ['running']
    );
    return row?.count || 0;
  }

  countPending(): number {
    const row = this.db.get<{ count: number }>(
      'SELECT COUNT(*) as count FROM tasks WHERE status = ?',
      ['pending']
    );
    return row?.count || 0;
  }

  countByProject(projectId: string, status?: TaskStatus): number {
    if (status) {
      const row = this.db.get<{ count: number }>(
        'SELECT COUNT(*) as count FROM tasks WHERE project_id = ? AND status = ?',
        [projectId, status]
      );
      return row?.count || 0;
    }
    const row = this.db.get<{ count: number }>(
      'SELECT COUNT(*) as count FROM tasks WHERE project_id = ?',
      [projectId]
    );
    return row?.count || 0;
  }
}
