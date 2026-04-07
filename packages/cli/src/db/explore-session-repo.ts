import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { ExploreSession, ExploreStatus } from '../types';

interface ExploreSessionRow {
  id: string;
  project_id: string;
  issue_id: string | null;
  title: string;
  status: string;
  created_at: string;
  updated_at: string;
}

function rowToExploreSession(row: ExploreSessionRow): ExploreSession {
  return {
    id: row.id,
    projectId: row.project_id,
    issueId: row.issue_id,
    title: row.title,
    status: row.status as ExploreStatus,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export interface CreateExploreSessionData {
  projectId: string;
  title: string;
}

export class ExploreSessionRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateExploreSessionData): ExploreSession {
    const now = new Date().toISOString();
    const id = uuidv4();

    this.db.run(
      `INSERT INTO explore_sessions (id, project_id, title, status, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [id, data.projectId, data.title, ExploreStatus.Active, now, now]
    );

    return {
      id,
      projectId: data.projectId,
      issueId: null,
      title: data.title,
      status: ExploreStatus.Active,
      createdAt: now,
      updatedAt: now,
    };
  }

  findById(id: string): ExploreSession | null {
    const row = this.db.get<ExploreSessionRow>(
      'SELECT * FROM explore_sessions WHERE id = ?',
      [id]
    );
    return row ? rowToExploreSession(row) : null;
  }

  findByProject(projectId: string): ExploreSession[] {
    const rows = this.db.all<ExploreSessionRow>(
      'SELECT * FROM explore_sessions WHERE project_id = ? ORDER BY updated_at DESC',
      [projectId]
    );
    return rows.map(rowToExploreSession);
  }

  delete(id: string): boolean {
    const result = this.db.run('DELETE FROM explore_sessions WHERE id = ?', [id]);
    return result.changes > 0;
  }

  updateStatus(id: string, status: ExploreStatus): ExploreSession | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE explore_sessions SET status = ?, updated_at = ? WHERE id = ?',
      [status, now, id]
    );
    return this.findById(id);
  }

  updateIssueId(id: string, issueId: string): ExploreSession | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE explore_sessions SET issue_id = ?, updated_at = ? WHERE id = ?',
      [issueId, now, id]
    );
    return this.findById(id);
  }

  crystallize(id: string, issueId: string): ExploreSession | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE explore_sessions SET issue_id = ?, status = ?, updated_at = ? WHERE id = ?',
      [issueId, ExploreStatus.Crystallized, now, id]
    );
    return this.findById(id);
  }

}
