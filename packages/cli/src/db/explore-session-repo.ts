import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { ExploreSession, ExploreStatus } from '../types';
import { load } from '../config/config-loader';

interface ExploreSessionRow {
  id: string;
  project_id: string;
  issue_id: string | null;
  title: string;
  status: string;
  model: string | null;
  variant: string | null;
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
    model: row.model ?? undefined,
    variant: row.variant ?? undefined,
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
    const config = load();
    const model = config.model ?? null;

    this.db.run(
      `INSERT INTO explore_sessions (id, project_id, title, status, model, variant, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, data.projectId, data.title, ExploreStatus.Active, model, null, now, now]
    );

    return {
      id,
      projectId: data.projectId,
      issueId: null,
      title: data.title,
      status: ExploreStatus.Active,
      model: model ?? undefined,
      variant: undefined,
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

  findByProject(projectId: string, status?: string): ExploreSession[] {
    if (status) {
      const rows = this.db.all<ExploreSessionRow>(
        'SELECT * FROM explore_sessions WHERE project_id = ? AND status = ? ORDER BY updated_at DESC',
        [projectId, status]
      );
      return rows.map(rowToExploreSession);
    }
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

  updateModel(id: string, model: string, variant: string | null): ExploreSession | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE explore_sessions SET model = ?, variant = ?, updated_at = ? WHERE id = ?',
      [model, variant, now, id]
    );
    return this.findById(id);
  }

  updateTitle(id: string, title: string): ExploreSession | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE explore_sessions SET title = ?, updated_at = ? WHERE id = ?',
      [title, now, id]
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
