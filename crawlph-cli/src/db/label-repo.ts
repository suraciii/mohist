import { DatabaseManager } from './database';

export class LabelRepo {
  constructor(private db: DatabaseManager) {}

  findAllUsed(projectId: string): string[] {
    const rows = this.db.all<{ labels: string }>(
      'SELECT labels FROM issues WHERE project_id = ?',
      [projectId]
    );
    
    const labelSet = new Set<string>();
    
    for (const row of rows) {
      try {
        const labels: string[] = JSON.parse(row.labels || '[]');
        labels.forEach(label => labelSet.add(label));
      } catch {
        // ignore parse errors
      }
    }
    
    return Array.from(labelSet).sort();
  }
  
  findAllUsedGlobally(): string[] {
    const rows = this.db.all<{ labels: string }>(
      'SELECT labels FROM issues'
    );
    
    const labelSet = new Set<string>();
    
    for (const row of rows) {
      try {
        const labels: string[] = JSON.parse(row.labels || '[]');
        labels.forEach(label => labelSet.add(label));
      } catch {
        // ignore parse errors
      }
    }
    
    return Array.from(labelSet).sort();
  }
}
