import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';

export interface WorkflowLogEntry {
  id: string;
  issueId: string;
  sessionId: string | null;
  eventType: string;
  data: string;
  createdAt: string;
}

interface WorkflowLogRow {
  id: string;
  issue_id: string;
  session_id: string | null;
  event_type: string;
  data: string;
  created_at: string;
}

function rowToEntry(row: WorkflowLogRow): WorkflowLogEntry {
  return {
    id: row.id,
    issueId: row.issue_id,
    sessionId: row.session_id,
    eventType: row.event_type,
    data: row.data,
    createdAt: row.created_at,
  };
}

export class WorkflowLogRepo {
  constructor(private db: DatabaseManager) {}

  insert(issueId: string, sessionId: string | null, eventType: string, data: object): WorkflowLogEntry {
    const id = uuidv4();
    const dataStr = JSON.stringify(data);

    this.db.run(
      `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [id, issueId, sessionId, eventType, dataStr, new Date().toISOString()]
    );

    const row = this.db.get<WorkflowLogRow>(
      'SELECT * FROM workflow_log WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back workflow_log entry after insert (id=${id})`);
    }

    return rowToEntry(row);
  }

  findByIssueId(issueId: string, eventType?: string): WorkflowLogEntry[] {
    if (eventType) {
      const rows = this.db.all<WorkflowLogRow>(
        `SELECT * FROM workflow_log WHERE issue_id = ? AND event_type = ? ORDER BY created_at ASC, rowid ASC`,
        [issueId, eventType]
      );
      return rows.map(rowToEntry);
    }

    const rows = this.db.all<WorkflowLogRow>(
      `SELECT * FROM workflow_log WHERE issue_id = ? ORDER BY created_at ASC, rowid ASC`,
      [issueId]
    );
    return rows.map(rowToEntry);
  }

  findById(id: string): WorkflowLogEntry | null {
    const row = this.db.get<WorkflowLogRow>(
      'SELECT * FROM workflow_log WHERE id = ?',
      [id]
    );
    return row ? rowToEntry(row) : null;
  }

  findBySessionId(sessionId: string): WorkflowLogEntry[] {
    const rows = this.db.all<WorkflowLogRow>(
      `SELECT * FROM workflow_log WHERE session_id = ? ORDER BY created_at ASC, rowid ASC`,
      [sessionId]
    );
    return rows.map(rowToEntry);
  }

  findBySessionIds(sessionIds: string[]): WorkflowLogEntry[] {
    if (sessionIds.length === 0) return [];
    const placeholders = sessionIds.map(() => '?').join(',');
    const rows = this.db.all<WorkflowLogRow>(
      `SELECT * FROM workflow_log WHERE session_id IN (${placeholders}) ORDER BY session_id, created_at ASC, rowid ASC`,
      sessionIds
    );
    return rows.map(rowToEntry);
  }
}
