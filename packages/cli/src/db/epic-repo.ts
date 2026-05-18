import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { Epic, EpicStatus, EpicPriority, LinkedIssue, IssueStatus, Stage, Priority } from '../types';

interface EpicRow {
  id: string;
  project_id: string;
  title: string;
  description: string;
  priority: string;
  status: string;
  created_at: string;
  updated_at: string;
}

interface IssueRow {
  id: string;
  number: number;
  title: string;
  status: string;
  stage: string;
  priority: string;
}

function rowToEpic(row: EpicRow): Epic {
  return {
    id: row.id,
    projectId: row.project_id,
    title: row.title,
    description: row.description,
    priority: row.priority as EpicPriority,
    status: row.status as EpicStatus,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function rowToLinkedIssue(row: IssueRow): LinkedIssue {
  return {
    id: row.id,
    number: row.number,
    title: row.title,
    status: row.status as IssueStatus,
    stage: row.stage as Stage,
    priority: row.priority as Priority,
  };
}

export interface CreateEpicData {
  projectId: string;
  title: string;
  description: string;
  priority: EpicPriority;
}

export class EpicRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateEpicData): Epic {
    const now = new Date().toISOString();
    const id = uuidv4();

    this.db.run(
      `INSERT INTO epics (id, project_id, title, description, priority, status, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, data.projectId, data.title, data.description, data.priority, EpicStatus.Active, now, now]
    );

    return {
      id,
      projectId: data.projectId,
      title: data.title,
      description: data.description,
      priority: data.priority,
      status: EpicStatus.Active,
      createdAt: now,
      updatedAt: now,
    };
  }

  findById(projectId: string, id: string): Epic | null {
    const row = this.db.get<EpicRow>(
      'SELECT * FROM epics WHERE project_id = ? AND id = ?',
      [projectId, id]
    );
    return row ? rowToEpic(row) : null;
  }

  findAll(projectId: string): Epic[] {
    const rows = this.db.all<EpicRow>(
      'SELECT * FROM epics WHERE project_id = ? ORDER BY created_at DESC',
      [projectId]
    );
    return rows.map(rowToEpic);
  }

  findByStatus(projectId: string, status: EpicStatus): Epic[] {
    const rows = this.db.all<EpicRow>(
      'SELECT * FROM epics WHERE project_id = ? AND status = ? ORDER BY created_at DESC',
      [projectId, status]
    );
    return rows.map(rowToEpic);
  }

  updateStatus(projectId: string, id: string, status: EpicStatus): Epic | null {
    const existing = this.findById(projectId, id);
    if (!existing) return null;

    const now = new Date().toISOString();
    this.db.run(
      'UPDATE epics SET status = ?, updated_at = ? WHERE project_id = ? AND id = ?',
      [status, now, projectId, id]
    );

    return this.findById(projectId, id);
  }

  delete(projectId: string, id: string): boolean {
    const result = this.db.run('DELETE FROM epics WHERE project_id = ? AND id = ?', [projectId, id]);
    return result.changes > 0;
  }

  addIssue(epicId: string, issueId: string): void {
    const now = new Date().toISOString();
    this.db.run(
      `INSERT INTO epic_issues (epic_id, issue_id, created_at) VALUES (?, ?, ?)`,
      [epicId, issueId, now]
    );
  }

  removeIssue(epicId: string, issueId: string): boolean {
    const result = this.db.run(
      'DELETE FROM epic_issues WHERE epic_id = ? AND issue_id = ?',
      [epicId, issueId]
    );
    return result.changes > 0;
  }

  getLinkedIssues(epicId: string): LinkedIssue[] {
    const rows = this.db.all<IssueRow>(
      `SELECT i.id, i.number, i.title, i.status, i.stage, i.priority
       FROM epic_issues ei
       JOIN issues i ON ei.issue_id = i.id
       WHERE ei.epic_id = ?
       ORDER BY ei.created_at ASC`,
      [epicId]
    );
    return rows.map(rowToLinkedIssue);
  }

  getLinkedIssueIds(epicId: string): string[] {
    const rows = this.db.all<{ issue_id: string }>(
      'SELECT issue_id FROM epic_issues WHERE epic_id = ?',
      [epicId]
    );
    return rows.map(r => r.issue_id);
  }

  getIssueEpic(projectId: string, issueId: string): Epic | null {
    const row = this.db.get<EpicRow & { epic_id: string }>(
      `SELECT e.* FROM epics e
       JOIN epic_issues ei ON e.id = ei.epic_id
       WHERE e.project_id = ? AND ei.issue_id = ?`,
      [projectId, issueId]
    );
    return row ? rowToEpic(row) : null;
  }

  getIssueEpicSummary(projectId: string, issueId: string): { id: string; title: string; status: EpicStatus; priority: EpicPriority } | null {
    const row = this.db.get<{ id: string; title: string; status: string; priority: string }>(
      `SELECT e.id, e.title, e.status, e.priority FROM epics e
       JOIN epic_issues ei ON e.id = ei.epic_id
       WHERE e.project_id = ? AND ei.issue_id = ?`,
      [projectId, issueId]
    );
    if (!row) return null;
    return {
      id: row.id,
      title: row.title,
      status: row.status as EpicStatus,
      priority: row.priority as EpicPriority,
    };
  }

  findEpicByIssueId(issueId: string): Epic | null {
    const row = this.db.get<EpicRow>(
      `SELECT e.* FROM epics e
       JOIN epic_issues ei ON e.id = ei.epic_id
       WHERE ei.issue_id = ?`,
      [issueId]
    );
    return row ? rowToEpic(row) : null;
  }
}
