import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { Comment } from '../types';

interface CommentRow {
  id: string;
  issue_id: string;
  body: string;
  created_at: string;
}

function rowToComment(row: CommentRow): Comment {
  return {
    id: row.id,
    issueId: row.issue_id,
    body: row.body,
    createdAt: row.created_at,
  };
}

export interface CreateCommentData {
  issueId: string;
  body: string;
}

export class CommentRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateCommentData): Comment {
    const now = new Date().toISOString();
    const id = uuidv4();
    
    this.db.run(
      `INSERT INTO comments (id, issue_id, body, created_at)
       VALUES (?, ?, ?, ?)`,
      [id, data.issueId, data.body, now]
    );
    
    return {
      id,
      issueId: data.issueId,
      body: data.body,
      createdAt: now,
    };
  }

  findById(id: string): Comment | null {
    const row = this.db.get<CommentRow>(
      'SELECT * FROM comments WHERE id = ?',
      [id]
    );
    return row ? rowToComment(row) : null;
  }

  findByIssue(issueId: string): Comment[] {
    const rows = this.db.all<CommentRow>(
      'SELECT * FROM comments WHERE issue_id = ? ORDER BY created_at ASC',
      [issueId]
    );
    return rows.map(rowToComment);
  }

  delete(id: string): boolean {
    const result = this.db.run('DELETE FROM comments WHERE id = ?', [id]);
    return result.changes > 0;
  }

  deleteByIssue(issueId: string): number {
    const result = this.db.run('DELETE FROM comments WHERE issue_id = ?', [issueId]);
    return result.changes;
  }
}
