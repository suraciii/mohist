import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';

export interface AgentSessionMessage {
  id: string;
  issueId: string;
  sessionId: string;
  role: string;
  content: string | null;
  toolCalls: string | null;
  toolCallId: string | null;
  toolName: string | null;
  toolResult: string | null;
  stepIndex: number;
  messageIndex: number;
  createdAt: string;
}

interface AgentSessionMessageRow {
  id: string;
  issue_id: string;
  session_id: string;
  role: string;
  content: string | null;
  tool_calls: string | null;
  tool_call_id: string | null;
  tool_name: string | null;
  tool_result: string | null;
  step_index: number;
  message_index: number;
  created_at: string;
}

function rowToAgentSessionMessage(row: AgentSessionMessageRow): AgentSessionMessage {
  return {
    id: row.id,
    issueId: row.issue_id,
    sessionId: row.session_id,
    role: row.role,
    content: row.content,
    toolCalls: row.tool_calls,
    toolCallId: row.tool_call_id,
    toolName: row.tool_name,
    toolResult: row.tool_result,
    stepIndex: row.step_index,
    messageIndex: row.message_index,
    createdAt: row.created_at,
  };
}

export interface CreateAgentSessionMessageData {
  issueId: string;
  sessionId: string;
  role: string;
  content?: string | null;
  toolCalls?: string | null;
  toolCallId?: string | null;
  toolName?: string | null;
  toolResult?: string | null;
  stepIndex: number;
  messageIndex: number;
}

export class AgentSessionMessageRepo {
  constructor(private db: DatabaseManager) {}

  insert(data: CreateAgentSessionMessageData): AgentSessionMessage {
    const id = uuidv4();
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO agent_session_message (id, issue_id, session_id, role, content, tool_calls, tool_call_id, tool_name, tool_result, step_index, message_index, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, data.issueId, data.sessionId, data.role, data.content ?? null, data.toolCalls ?? null, data.toolCallId ?? null, data.toolName ?? null, data.toolResult ?? null, data.stepIndex, data.messageIndex, now]
    );

    const row = this.db.get<AgentSessionMessageRow>(
      'SELECT * FROM agent_session_message WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back agent_session_message entry after insert (id=${id})`);
    }

    return rowToAgentSessionMessage(row);
  }

  findByIssueId(issueId: string): AgentSessionMessage[] {
    const rows = this.db.all<AgentSessionMessageRow>(
      `SELECT * FROM agent_session_message WHERE issue_id = ? ORDER BY step_index ASC, message_index ASC`,
      [issueId]
    );
    return rows.map(rowToAgentSessionMessage);
  }
}
