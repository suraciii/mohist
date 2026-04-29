import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, type SqlValue } from './database';

export interface Skill {
  id: string;
  name: string;
  projectId: string;
  description: string;
  prompt: string;
  dirPath: string;
  createdAt: string;
  updatedAt: string;
}

interface SkillRow {
  id: string;
  name: string;
  project_id: string;
  description: string;
  prompt: string;
  dir_path: string;
  created_at: string;
  updated_at: string;
}

function rowToSkill(row: SkillRow): Skill {
  return {
    id: row.id,
    name: row.name,
    projectId: row.project_id,
    description: row.description,
    prompt: row.prompt,
    dirPath: row.dir_path,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export interface CreateSkillData {
  name: string;
  projectId: string;
  description: string;
  prompt: string;
  dirPath: string;
}

export class SkillRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateSkillData): Skill {
    const id = uuidv4();
    const now = new Date().toISOString();

    this.db.run(
      `INSERT INTO skills (id, name, project_id, description, prompt, dir_path, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, data.name, data.projectId, data.description, data.prompt, data.dirPath, now, now]
    );

    const row = this.db.get<SkillRow>(
      'SELECT * FROM skills WHERE id = ?',
      [id]
    );

    if (!row) {
      throw new Error(`Failed to read back skill after insert (id=${id})`);
    }

    return rowToSkill(row);
  }

  findById(id: string): Skill | null {
    const row = this.db.get<SkillRow>(
      'SELECT * FROM skills WHERE id = ?',
      [id]
    );
    return row ? rowToSkill(row) : null;
  }

  findByName(name: string): Skill | null {
    const row = this.db.get<SkillRow>(
      'SELECT * FROM skills WHERE name = ?',
      [name]
    );
    return row ? rowToSkill(row) : null;
  }

  findByProject(projectId: string): Skill[] {
    const rows = this.db.all<SkillRow>(
      'SELECT * FROM skills WHERE project_id = ? ORDER BY name ASC',
      [projectId]
    );
    return rows.map(rowToSkill);
  }

  update(id: string, data: { description?: string; prompt?: string }): Skill | null {
    const now = new Date().toISOString();
    const sets: string[] = [];
    const values: SqlValue[] = [];

    if (data.description !== undefined) {
      sets.push('description = ?');
      values.push(data.description);
    }
    if (data.prompt !== undefined) {
      sets.push('prompt = ?');
      values.push(data.prompt);
    }

    if (sets.length === 0) {
      return this.findById(id);
    }

    sets.push('updated_at = ?');
    values.push(now);
    values.push(id);

    this.db.run(
      `UPDATE skills SET ${sets.join(', ')} WHERE id = ?`,
      values
    );

    return this.findById(id);
  }

  delete(id: string): boolean {
    const result = this.db.run('DELETE FROM skills WHERE id = ?', [id]);
    return result.changes > 0;
  }
}
