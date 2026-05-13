import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';

export interface SessionStreamLogEntry {
  id: string;
  sessionId: string;
  issueId: string;
  eventType: string;
  data: string;
  createdAt: string;
}

interface SessionStreamLogRow {
  id: string;
  session_id: string;
  issue_id: string;
  event_type: string;
  data: string;
  created_at: string;
}

function rowToEntry(row: SessionStreamLogRow): SessionStreamLogEntry {
  return {
    id: row.id,
    sessionId: row.session_id,
    issueId: row.issue_id,
    eventType: row.event_type,
    data: row.data,
    createdAt: row.created_at,
  };
}

export class SessionStreamLogRepo {
  constructor(private db: DatabaseManager) {}

  insert(issueId: string, sessionId: string, eventType: string, data: object): SessionStreamLogEntry {
    const id = uuidv4();
    const dataStr = JSON.stringify(data);
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [id, sessionId, issueId, eventType, dataStr, now]
    );

    const row = this.db.get<SessionStreamLogRow>(
      'SELECT * FROM session_stream_log WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back session_stream_log entry after insert (id=${id})`);
    }

    return rowToEntry(row);
  }

  findBySessionId(sessionId: string): SessionStreamLogEntry[] {
    const rows = this.db.all<SessionStreamLogRow>(
      `SELECT * FROM session_stream_log WHERE session_id = ? ORDER BY created_at ASC, rowid ASC`,
      [sessionId]
    );
    return rows.map(rowToEntry);
  }

  findByIssueId(issueId: string): SessionStreamLogEntry[] {
    const rows = this.db.all<SessionStreamLogRow>(
      `SELECT * FROM session_stream_log WHERE issue_id = ? ORDER BY created_at ASC, rowid ASC`,
      [issueId]
    );
    return rows.map(rowToEntry);
  }
}
