import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, type SqlValue } from './database';

export interface CoderSession {
  id: string;
  issueId: string;
  acpSessionId: string;
  executionId: string | null;
  taskDescription: string | null;
  status: string;
  createdAt: string;
  completedAt: string | null;
  model: string | null;
  coderType: string | null;
  stage: string | null;
  title: string | null;
}

interface CoderSessionRow {
  id: string;
  issue_id: string;
  acp_session_id: string;
  execution_id: string | null;
  task_description: string | null;
  status: string;
  created_at: string;
  completed_at: string | null;
  model: string | null;
  coder_type: string | null;
  stage: string | null;
  title: string | null;
}

function rowToCoderSession(row: CoderSessionRow): CoderSession {
  return {
    id: row.id,
    issueId: row.issue_id,
    acpSessionId: row.acp_session_id,
    executionId: row.execution_id,
    taskDescription: row.task_description,
    status: row.status,
    createdAt: row.created_at,
    completedAt: row.completed_at,
    model: row.model,
    coderType: row.coder_type,
    stage: row.stage,
    title: row.title,
  };
}

export interface CreateCoderSessionData {
  issueId: string;
  acpSessionId: string;
  executionId?: string | null;
  taskDescription?: string | null;
  model?: string;
  coderType?: string;
  stage?: string;
  title?: string;
}

export interface SessionWithIssueInfo {
  issueNumber: number;
  issueTitle: string;
  issueStage: string;
  sessionId: string;
  status: string;
  model: string | null;
  taskDescription: string | null;
  createdAt: string;
  completedAt: string | null;
  lastActivityAt: string | null;
  title: string | null;
}

interface SessionWithIssueInfoRow {
  issue_number: number;
  issue_title: string;
  issue_stage: string;
  session_id: string;
  status: string;
  model: string | null;
  task_description: string | null;
  created_at: string;
  completed_at: string | null;
  last_activity_at: string | null;
  title: string | null;
}

export class CoderSessionRepo {
  constructor(private db: DatabaseManager) {}

  findAllWithIssueInfo(projectId: string, status?: string, limit: number = 50): SessionWithIssueInfo[] {
    const statusFilter = status ? ' AND cs.status = ?' : '';
    const params: SqlValue[] = [projectId];
    if (status) params.push(status);
    params.push(limit);

    const rows = this.db.all<SessionWithIssueInfoRow>(
      `SELECT i.number AS issue_number, i.title AS issue_title, i.stage AS issue_stage,
        cs.id AS session_id, cs.status, cs.model, cs.task_description,
        cs.created_at, cs.completed_at, cs.title,
        (SELECT wl.created_at FROM workflow_log wl WHERE wl.session_id = cs.acp_session_id ORDER BY wl.created_at DESC LIMIT 1) AS last_activity_at
      FROM coder_session cs
      JOIN issues i ON cs.issue_id = i.id
      WHERE i.project_id = ?${statusFilter}
      ORDER BY cs.created_at DESC
      LIMIT ?`,
      params
    );

    return rows.map((row) => ({
      issueNumber: row.issue_number,
      issueTitle: row.issue_title,
      issueStage: row.issue_stage,
      sessionId: row.session_id,
      status: row.status,
      model: row.model,
      taskDescription: row.task_description ? row.task_description.slice(0, 200) : null,
      createdAt: row.created_at,
      completedAt: row.completed_at,
      lastActivityAt: row.last_activity_at,
      title: row.title,
    }));
  }

  insert(data: CreateCoderSessionData): CoderSession {
    const id = uuidv4();
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO coder_session (id, issue_id, acp_session_id, execution_id, task_description, status, created_at, completed_at, model, coder_type, stage, title)
       VALUES (?, ?, ?, ?, ?, 'running', ?, NULL, ?, ?, ?, ?)`,
      [id, data.issueId, data.acpSessionId, data.executionId ?? null, data.taskDescription ?? null, now, data.model ?? null, data.coderType ?? null, data.stage ?? null, data.title ?? null]
    );

    const row = this.db.get<CoderSessionRow>(
      'SELECT * FROM coder_session WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back coder_session entry after insert (id=${id})`);
    }

    return rowToCoderSession(row);
  }

  updateStatus(id: string, status: string): CoderSession {
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE coder_session SET status = ?, completed_at = ? WHERE id = ?',
      [status, now, id]
    );

    const row = this.db.get<CoderSessionRow>(
      'SELECT * FROM coder_session WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back coder_session entry after update (id=${id})`);
    }

    return rowToCoderSession(row);
  }

  failRunningByIssueId(issueId: string): number {
    const now = new Date().toISOString();
    const result = this.db.run(
      "UPDATE coder_session SET status = 'failed', completed_at = ? WHERE issue_id = ? AND status = 'running'",
      [now, issueId]
    );
    return result.changes;
  }

  findByIssueId(issueId: string): CoderSession[] {
    const rows = this.db.all<CoderSessionRow>(
      'SELECT * FROM coder_session WHERE issue_id = ? ORDER BY created_at ASC',
      [issueId]
    );
    return rows.map(rowToCoderSession);
  }
}
