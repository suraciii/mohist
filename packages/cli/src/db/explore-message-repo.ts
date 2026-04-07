import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { ExploreMessage, ToolCallRecord } from '../types';

interface ExploreMessageRow {
  id: string;
  session_id: string;
  role: string;
  content: string;
  tool_calls: string | null;
  created_at: string;
}

function rowToExploreMessage(row: ExploreMessageRow): ExploreMessage {
  let toolCalls: ToolCallRecord[] | null = null;
  if (row.tool_calls) {
    try {
      toolCalls = JSON.parse(row.tool_calls);
    } catch {
      toolCalls = null;
    }
  }

  return {
    id: row.id,
    sessionId: row.session_id,
    role: row.role as 'user' | 'assistant',
    content: row.content,
    toolCalls,
    createdAt: row.created_at,
  };
}

export interface CreateExploreMessageData {
  sessionId: string;
  role: 'user' | 'assistant';
  content: string;
  toolCalls?: ToolCallRecord[];
}

export class ExploreMessageRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateExploreMessageData): ExploreMessage {
    const now = new Date().toISOString();
    const id = uuidv4();
    const toolCallsJson = data.toolCalls ? JSON.stringify(data.toolCalls) : null;

    this.db.run(
      `INSERT INTO explore_messages (id, session_id, role, content, tool_calls, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [id, data.sessionId, data.role, data.content, toolCallsJson, now]
    );

    return {
      id,
      sessionId: data.sessionId,
      role: data.role,
      content: data.content,
      toolCalls: data.toolCalls ?? null,
      createdAt: now,
    };
  }

  findBySession(sessionId: string): ExploreMessage[] {
    const rows = this.db.all<ExploreMessageRow>(
      'SELECT * FROM explore_messages WHERE session_id = ? ORDER BY created_at ASC',
      [sessionId]
    );
    return rows.map(rowToExploreMessage);
  }

  deleteBySession(sessionId: string): number {
    const result = this.db.run('DELETE FROM explore_messages WHERE session_id = ?', [sessionId]);
    return result.changes;
  }
}
