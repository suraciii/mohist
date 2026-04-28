import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, SqlValue } from './database';
import { Issue, Stage, IssueStatus, ApprovalState, MergeState, Priority } from '../types';

interface IssueRow {
  id: string;
  number: number;
  project_id: string;
  title: string;
  body: string | null;
  stage: string;
  status: string;
  labels: string;
  priority: string;
  created_at: string;
  updated_at: string;
  approval_state: string | null;
  merge_state: string | null;
  conflict_retry_count: number | null;
}

function rowToIssue(row: IssueRow): Issue {
  let labels: string[] = [];
  try {
    labels = JSON.parse(row.labels || '[]');
  } catch {
    labels = [];
  }

  let approvalState: ApprovalState | undefined;
  if (row.approval_state) {
    try {
      approvalState = JSON.parse(row.approval_state);
    } catch {
      approvalState = undefined;
    }
  }

  return {
    id: row.id,
    number: row.number,
    title: row.title,
    body: row.body ? (typeof row.body === 'string' ? row.body : Buffer.from(row.body).toString('utf-8')) : undefined,
    stage: row.stage as Stage,
    status: row.status as IssueStatus,
    projectId: row.project_id,
    labels,
    priority: (row.priority || 'p2') as Priority,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    approvalState,
    mergeState: (row.merge_state as MergeState) || undefined,
    conflictRetryCount: row.conflict_retry_count ?? undefined,
  };
}

export interface CreateIssueData {
  number: number;
  projectId: string;
  title: string;
  body?: string;
  labels?: string[];
  priority?: Priority;
}

export interface IssueQueryOptions {
  projectId?: string;
  stage?: Stage;
  status?: IssueStatus;
  priority?: Priority;
}

export class IssueRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateIssueData): Issue {
    const now = new Date().toISOString();
    const id = uuidv4();
    const labels = JSON.stringify(data.labels || []);
    const priority = data.priority || 'p2';
    
    this.db.run(
      `INSERT INTO issues (id, number, project_id, title, body, stage, status, labels, priority, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, data.number, data.projectId, data.title, data.body || null, Stage.Backlog, IssueStatus.Active, labels, priority, now, now]
    );
    
    return {
      id,
      number: data.number,
      title: data.title,
      body: data.body,
      stage: Stage.Backlog,
      status: IssueStatus.Active,
      projectId: data.projectId,
      labels: data.labels || [],
      priority: priority as Priority,
      createdAt: now,
      updatedAt: now,
    };
  }

  findById(id: string): Issue | null {
    const row = this.db.get<IssueRow>(
      'SELECT * FROM issues WHERE id = ?',
      [id]
    );
    return row ? rowToIssue(row) : null;
  }

  findByNumber(projectId: string, number: number): Issue | null {
    const row = this.db.get<IssueRow>(
      'SELECT * FROM issues WHERE project_id = ? AND number = ?',
      [projectId, number]
    );
    return row ? rowToIssue(row) : null;
  }

  findAll(options: IssueQueryOptions = {}): Issue[] {
    let sql = 'SELECT * FROM issues WHERE 1=1';
    const params: SqlValue[] = [];
    
    if (options.projectId) {
      sql += ' AND project_id = ?';
      params.push(options.projectId);
    }
    if (options.stage) {
      sql += ' AND stage = ?';
      params.push(options.stage);
    }
    if (options.status) {
      sql += ' AND status = ?';
      params.push(options.status);
    }
    if (options.priority) {
      sql += ' AND priority = ?';
      params.push(options.priority);
    }
    
    sql += ' ORDER BY priority ASC, number ASC';
    
    const rows = this.db.all<IssueRow>(sql, params);
    return rows.map(rowToIssue);
  }

  findByStage(projectId: string, stage: Stage): Issue[] {
    return this.findAll({ projectId, stage });
  }

  findByStatus(projectId: string, status: IssueStatus): Issue[] {
    return this.findAll({ projectId, status });
  }

  findActive(projectId: string): Issue[] {
    return this.findAll({ projectId, status: IssueStatus.Active });
  }

  updateStage(issueId: string, stage: Stage): Issue | null {
    const now = new Date().toISOString();
    
    this.db.run(
      'UPDATE issues SET stage = ?, updated_at = ? WHERE id = ?',
      [stage, now, issueId]
    );
    
    return this.findById(issueId);
  }

  updateStatus(issueId: string, status: IssueStatus): Issue | null {
    const now = new Date().toISOString();
    
    this.db.run(
      'UPDATE issues SET status = ?, updated_at = ? WHERE id = ?',
      [status, now, issueId]
    );
    
    return this.findById(issueId);
  }

  update(issueId: string, data: Partial<{ title: string; body: string; stage: Stage; status: IssueStatus; labels: string[]; mergeState: MergeState; priority: Priority }>): Issue | null {
    const existing = this.findById(issueId);
    if (!existing) return null;
    
    const updates: string[] = [];
    const values: SqlValue[] = [];
    
    if (data.title !== undefined) {
      updates.push('title = ?');
      values.push(data.title);
    }
    if (data.body !== undefined) {
      updates.push('body = ?');
      values.push(data.body || null);
    }
    if (data.stage !== undefined) {
      updates.push('stage = ?');
      values.push(data.stage);
    }
    if (data.status !== undefined) {
      updates.push('status = ?');
      values.push(data.status);
    }
    if (data.labels !== undefined) {
      updates.push('labels = ?');
      values.push(JSON.stringify(data.labels));
    }
    if (data.mergeState !== undefined) {
      updates.push('merge_state = ?');
      values.push(data.mergeState);
    }
    if (data.priority !== undefined) {
      updates.push('priority = ?');
      values.push(data.priority);
    }
    
    if (updates.length === 0) return existing;
    
    updates.push('updated_at = ?');
    values.push(new Date().toISOString());
    values.push(issueId);
    
    this.db.run(
      `UPDATE issues SET ${updates.join(', ')} WHERE id = ?`,
      values
    );
    
    return this.findById(issueId);
  }
  
  addLabel(issueId: string, label: string): Issue | null {
    const issue = this.findById(issueId);
    if (!issue) return null;
    
    const labels = [...new Set([...issue.labels, label])];
    return this.update(issueId, { labels });
  }
  
  removeLabel(issueId: string, label: string): Issue | null {
    const issue = this.findById(issueId);
    if (!issue) return null;
    
    const labels = issue.labels.filter(l => l !== label);
    return this.update(issueId, { labels });
  }

  delete(issueId: string): boolean {
    const result = this.db.run('DELETE FROM issues WHERE id = ?', [issueId]);
    return result.changes > 0;
  }

  deleteCascade(issueId: string): boolean {
    return this.delete(issueId);
  }

  deleteByProject(projectId: string): number {
    const result = this.db.run('DELETE FROM issues WHERE project_id = ?', [projectId]);
    return result.changes;
  }

  deleteByProjectCascade(projectId: string): number {
    return this.deleteByProject(projectId);
  }

  getNextNumber(projectId: string): number {
    const row = this.db.get<{ max: number | null }>(
      'SELECT MAX(number) as max FROM issues WHERE project_id = ?',
      [projectId]
    );
    return (row?.max || 0) + 1;
  }

  setApprovalState(issueId: string, approvalState: ApprovalState): Issue | null {
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE issues SET approval_state = ?, updated_at = ? WHERE id = ?',
      [JSON.stringify(approvalState), now, issueId]
    );

    return this.findById(issueId);
  }

  clearApprovalState(issueId: string): Issue | null {
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE issues SET approval_state = NULL, updated_at = ? WHERE id = ?',
      [now, issueId]
    );

    return this.findById(issueId);
  }

  setMergeState(issueId: string, mergeState: MergeState): Issue | null {
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE issues SET merge_state = ?, updated_at = ? WHERE id = ?',
      [mergeState, now, issueId]
    );

    return this.findById(issueId);
  }

  findByMergeStates(states: MergeState[]): Issue[] {
    if (states.length === 0) return [];
    const placeholders = states.map(() => '?').join(', ');
    const rows = this.db.all<IssueRow>(
      `SELECT * FROM issues WHERE merge_state IN (${placeholders})`,
      states
    );
    return rows.map(rowToIssue);
  }

  updateConflictRetryCount(issueId: string, count: number): Issue | null {
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE issues SET conflict_retry_count = ?, updated_at = ? WHERE id = ?',
      [count, now, issueId]
    );

    return this.findById(issueId);
  }

  findPendingApproval(projectId: string): Issue | null {
    const row = this.db.get<IssueRow>(
      `SELECT * FROM issues WHERE project_id = ? AND approval_state IS NOT NULL`,
      [projectId]
    );
    if (!row) return null;
    const issue = rowToIssue(row);
    if (issue.approvalState && issue.approvalState.status === 'awaiting') {
      return issue;
    }
    return null;
  }

  findPendingApprovalByIssueId(issueId: string): Issue | null {
    const row = this.db.get<IssueRow>(
      `SELECT * FROM issues WHERE id = ? AND approval_state IS NOT NULL`,
      [issueId]
    );
    if (!row) return null;
    const issue = rowToIssue(row);
    if (issue.approvalState && issue.approvalState.status === 'awaiting') {
      return issue;
    }
    return null;
  }

  count(projectId?: string): number {
    if (projectId) {
      const row = this.db.get<{ count: number }>(
        'SELECT COUNT(*) as count FROM issues WHERE project_id = ?',
        [projectId]
      );
      return row?.count || 0;
    }
    const row = this.db.get<{ count: number }>('SELECT COUNT(*) as count FROM issues');
    return row?.count || 0;
  }
}
