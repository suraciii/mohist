import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { Question, QuestionStatus } from '../types';

interface QuestionRow {
  id: string;
  issue_id: string;
  question: string;
  answer: string | null;
  status: string;
  created_at: string;
  answered_at: string | null;
}

function rowToQuestion(row: QuestionRow): Question {
  return {
    id: row.id,
    issueId: row.issue_id,
    question: row.question,
    answer: row.answer ?? undefined,
    status: row.status as QuestionStatus,
    createdAt: row.created_at,
    answeredAt: row.answered_at ?? undefined,
  };
}

export class QuestionRepo {
  constructor(private db: DatabaseManager) {}

  create(issueId: string, question: string): Question {
    const now = new Date().toISOString();
    const id = uuidv4();

    this.db.run(
      `INSERT INTO questions (id, issue_id, question, status, created_at)
       VALUES (?, ?, ?, 'pending', ?)`,
      [id, issueId, question, now]
    );

    return {
      id,
      issueId,
      question,
      status: 'pending',
      createdAt: now,
    };
  }

  answer(id: string, answer: string): Question | null {
    const now = new Date().toISOString();

    this.db.run(
      `UPDATE questions SET answer = ?, status = 'answered', answered_at = ? WHERE id = ?`,
      [answer, now, id]
    );

    const row = this.db.get<QuestionRow>(
      'SELECT * FROM questions WHERE id = ?',
      [id]
    );
    return row ? rowToQuestion(row) : null;
  }

  findById(id: string): Question | null {
    const row = this.db.get<QuestionRow>(
      'SELECT * FROM questions WHERE id = ?',
      [id]
    );
    return row ? rowToQuestion(row) : null;
  }

  findByIssueId(issueId: string): Question[] {
    const rows = this.db.all<QuestionRow>(
      'SELECT * FROM questions WHERE issue_id = ? ORDER BY created_at DESC',
      [issueId]
    );
    return rows.map(rowToQuestion);
  }

  findPendingByIssueId(issueId: string): Question[] {
    const rows = this.db.all<QuestionRow>(
      `SELECT * FROM questions WHERE issue_id = ? AND status = 'pending' ORDER BY created_at DESC`,
      [issueId]
    );
    return rows.map(rowToQuestion);
  }

  expire(id: string): Question | null {
    this.db.run(
      `UPDATE questions SET status = 'expired' WHERE id = ? AND status = 'pending'`,
      [id]
    );

    const row = this.db.get<QuestionRow>(
      'SELECT * FROM questions WHERE id = ?',
      [id]
    );
    return row ? rowToQuestion(row) : null;
  }

  expireAllPending(): number {
    const result = this.db.run(
      `UPDATE questions SET status = 'expired' WHERE status = 'pending'`
    );
    return result.changes;
  }
}
