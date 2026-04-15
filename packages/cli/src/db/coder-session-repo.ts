import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';

export interface CoderSession {
  id: string;
  issueId: string;
  acpSessionId: string;
  executionId: string | null;
  taskDescription: string | null;
  status: string;
  createdAt: string;
  completedAt: string | null;
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
  };
}

export interface CreateCoderSessionData {
  issueId: string;
  acpSessionId: string;
  executionId?: string | null;
  taskDescription?: string | null;
}

export class CoderSessionRepo {
  constructor(private db: DatabaseManager) {}

  insert(data: CreateCoderSessionData): CoderSession {
    const id = uuidv4();
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO coder_session (id, issue_id, acp_session_id, execution_id, task_description, status, created_at, completed_at)
       VALUES (?, ?, ?, ?, ?, 'running', ?, NULL)`,
      [id, data.issueId, data.acpSessionId, data.executionId ?? null, data.taskDescription ?? null, now]
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

  findByIssueId(issueId: string): CoderSession[] {
    const rows = this.db.all<CoderSessionRow>(
      'SELECT * FROM coder_session WHERE issue_id = ? ORDER BY created_at ASC',
      [issueId]
    );
    return rows.map(rowToCoderSession);
  }
}
