import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, type SqlValue } from './database';

export interface SkillRun {
  id: string;
  skillId: string;
  projectId: string;
  status: string;
  output: string | null;
  error: string | null;
  issueId: string | null;
  startedAt: string;
  completedAt: string | null;
}

interface SkillRunRow {
  id: string;
  skill_id: string;
  project_id: string;
  status: string;
  output: string | null;
  error: string | null;
  issue_id: string | null;
  started_at: string;
  completed_at: string | null;
}

function rowToSkillRun(row: SkillRunRow): SkillRun {
  return {
    id: row.id,
    skillId: row.skill_id,
    projectId: row.project_id,
    status: row.status,
    output: row.output,
    error: row.error,
    issueId: row.issue_id,
    startedAt: row.started_at,
    completedAt: row.completed_at,
  };
}

export interface CreateSkillRunData {
  skillId: string;
  projectId: string;
}

export class SkillRunRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateSkillRunData): SkillRun {
    const id = uuidv4();
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO skill_runs (id, skill_id, project_id, status, output, error, issue_id, started_at, completed_at)
       VALUES (?, ?, ?, 'running', NULL, NULL, NULL, ?, NULL)`,
      [id, data.skillId, data.projectId, now]
    );

    const row = this.db.get<SkillRunRow>(
      'SELECT * FROM skill_runs WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back skill_run after insert (id=${id})`);
    }

    return rowToSkillRun(row);
  }

  update(id: string, data: {
    status?: string;
    output?: string | null;
    error?: string | null;
    issueId?: string | null;
  }): SkillRun | null {
    const now = new Date().toISOString();
    const sets: string[] = [];
    const values: SqlValue[] = [];

    if (data.status !== undefined) {
      sets.push('status = ?');
      values.push(data.status);
    }
    if (data.output !== undefined) {
      sets.push('output = ?');
      values.push(data.output);
    }
    if (data.error !== undefined) {
      sets.push('error = ?');
      values.push(data.error);
    }
    if (data.issueId !== undefined) {
      sets.push('issue_id = ?');
      values.push(data.issueId);
    }

    if (data.status === 'completed' || data.status === 'failed') {
      sets.push('completed_at = ?');
      values.push(now);
    }

    if (sets.length === 0) {
      return this.findById(id);
    }

    values.push(id);

    this.db.run(
      `UPDATE skill_runs SET ${sets.join(', ')} WHERE id = ?`,
      values
    );

    return this.findById(id);
  }

  findById(id: string): SkillRun | null {
    const row = this.db.get<SkillRunRow>(
      'SELECT * FROM skill_runs WHERE id = ?',
      [id]
    );
    return row ? rowToSkillRun(row) : null;
  }

  findBySkillId(skillId: string): SkillRun[] {
    const rows = this.db.all<SkillRunRow>(
      'SELECT * FROM skill_runs WHERE skill_id = ? ORDER BY started_at DESC',
      [skillId]
    );
    return rows.map(rowToSkillRun);
  }
}
