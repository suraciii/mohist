import { DatabaseManager } from './database';
import { IssueStartPrerequisite } from '../types';

interface IssueStartPrerequisiteRow {
  issue_id: string;
  prerequisite_issue_id: string;
  created_at: string;
}

function rowToIssueStartPrerequisite(row: IssueStartPrerequisiteRow): IssueStartPrerequisite {
  return {
    issueId: row.issue_id,
    prerequisiteIssueId: row.prerequisite_issue_id,
    createdAt: row.created_at,
  };
}

export class IssueStartPrerequisiteRepo {
  constructor(private db: DatabaseManager) {}

  create(issueId: string, prerequisiteIssueId: string): IssueStartPrerequisite {
    const now = new Date().toISOString();
    this.db.run(
      `INSERT INTO issue_start_prerequisites (issue_id, prerequisite_issue_id, created_at)
       VALUES (?, ?, ?)`,
      [issueId, prerequisiteIssueId, now]
    );
    return {
      issueId,
      prerequisiteIssueId,
      createdAt: now,
    };
  }

  findById(issueId: string, prerequisiteIssueId: string): IssueStartPrerequisite | null {
    const row = this.db.get<IssueStartPrerequisiteRow>(
      'SELECT * FROM issue_start_prerequisites WHERE issue_id = ? AND prerequisite_issue_id = ?',
      [issueId, prerequisiteIssueId]
    );
    return row ? rowToIssueStartPrerequisite(row) : null;
  }

  findByIssue(issueId: string): IssueStartPrerequisite[] {
    const rows = this.db.all<IssueStartPrerequisiteRow>(
      'SELECT * FROM issue_start_prerequisites WHERE issue_id = ?',
      [issueId]
    );
    return rows.map(rowToIssueStartPrerequisite);
  }

  findByPrerequisite(prerequisiteIssueId: string): IssueStartPrerequisite[] {
    const rows = this.db.all<IssueStartPrerequisiteRow>(
      'SELECT * FROM issue_start_prerequisites WHERE prerequisite_issue_id = ?',
      [prerequisiteIssueId]
    );
    return rows.map(rowToIssueStartPrerequisite);
  }

  findAllByIssues(issueIds: string[]): IssueStartPrerequisite[] {
    if (issueIds.length === 0) return [];
    const placeholders = issueIds.map(() => '?').join(', ');
    const rows = this.db.all<IssueStartPrerequisiteRow>(
      `SELECT * FROM issue_start_prerequisites WHERE issue_id IN (${placeholders})`,
      issueIds
    );
    return rows.map(rowToIssueStartPrerequisite);
  }

  delete(issueId: string, prerequisiteIssueId: string): boolean {
    const result = this.db.run(
      'DELETE FROM issue_start_prerequisites WHERE issue_id = ? AND prerequisite_issue_id = ?',
      [issueId, prerequisiteIssueId]
    );
    return result.changes > 0;
  }

  deleteByIssue(issueId: string): number {
    const result = this.db.run('DELETE FROM issue_start_prerequisites WHERE issue_id = ?', [issueId]);
    return result.changes;
  }

  deleteByPrerequisite(prerequisiteIssueId: string): number {
    const result = this.db.run('DELETE FROM issue_start_prerequisites WHERE prerequisite_issue_id = ?', [prerequisiteIssueId]);
    return result.changes;
  }

  exists(issueId: string, prerequisiteIssueId: string): boolean {
    const row = this.db.get<{ count: number }>(
      'SELECT COUNT(*) as count FROM issue_start_prerequisites WHERE issue_id = ? AND prerequisite_issue_id = ?',
      [issueId, prerequisiteIssueId]
    );
    return (row?.count || 0) > 0;
  }
}