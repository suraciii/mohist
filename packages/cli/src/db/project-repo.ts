import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager, SqlValue } from './database';
import { Project } from '../types';

interface ProjectRow {
  id: string;
  name: string;
  path: string;
  created_at: string;
  updated_at: string;
}

function rowToProject(row: ProjectRow): Project {
  return {
    id: row.id,
    name: row.name,
    path: row.path,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}



export class ProjectRepo {
  constructor(private db: DatabaseManager) {}

  create(data: Omit<Project, 'id' | 'createdAt' | 'updatedAt'>): Project {
    const now = new Date().toISOString();
    const id = uuidv4();
    
    this.db.run(
      `INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)`,
      [id, data.name, data.path, now, now]
    );
    
    return {
      id,
      name: data.name,
      path: data.path,
      createdAt: now,
      updatedAt: now,
    };
  }

  findById(id: string): Project | null {
    const row = this.db.get<ProjectRow>(
      'SELECT * FROM projects WHERE id = ?',
      [id]
    );
    return row ? rowToProject(row) : null;
  }

  findByName(name: string): Project | null {
    const row = this.db.get<ProjectRow>(
      'SELECT * FROM projects WHERE name = ?',
      [name]
    );
    return row ? rowToProject(row) : null;
  }

  findByPath(path: string): Project | null {
    const row = this.db.get<ProjectRow>(
      'SELECT * FROM projects WHERE path = ?',
      [path]
    );
    return row ? rowToProject(row) : null;
  }

  findAll(): Project[] {
    const rows = this.db.all<ProjectRow>('SELECT * FROM projects ORDER BY name');
    return rows.map(rowToProject);
  }

  update(id: string, data: Partial<Omit<Project, 'id' | 'createdAt'>>): Project | null {
    const existing = this.findById(id);
    if (!existing) return null;
    
    const updates: string[] = [];
    const values: SqlValue[] = [];
    
    if (data.name !== undefined) {
      updates.push('name = ?');
      values.push(data.name);
    }
    if (data.path !== undefined) {
      updates.push('path = ?');
      values.push(data.path);
    }
    
    if (updates.length === 0) return existing;
    
    updates.push('updated_at = ?');
    values.push(new Date().toISOString());
    values.push(id);
    
    this.db.run(
      `UPDATE projects SET ${updates.join(', ')} WHERE id = ?`,
      values
    );
    
    return this.findById(id);
  }

  delete(id: string): boolean {
    const result = this.db.run('DELETE FROM projects WHERE id = ?', [id]);
    return result.changes > 0;
  }

  count(): number {
    const row = this.db.get<{ count: number }>('SELECT COUNT(*) as count FROM projects');
    return row?.count || 0;
  }
}
