import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';
import { CheckSuite, CheckSuiteChecks, CheckSuiteStatus, CheckState } from '../types';

interface CheckSuiteRow {
  id: string;
  issue_id: string;
  snapshot_sha: string;
  status: string;
  checks: string;
  created_at: string;
  updated_at: string;
}

function rowToCheckSuite(row: CheckSuiteRow): CheckSuite {
  return {
    id: row.id,
    issueId: row.issue_id,
    snapshotSha: row.snapshot_sha,
    status: row.status as CheckSuiteStatus,
    checks: JSON.parse(row.checks) as CheckSuiteChecks,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function makeInitialChecks(): CheckSuiteChecks {
  return {
    'build-test': { status: 'pending' },
    'ai-review': { status: 'pending' },
  };
}

export interface CreateCheckSuiteData {
  issueId: string;
  snapshotSha: string;
}

export class CheckSuiteRepo {
  constructor(private db: DatabaseManager) {}

  create(data: CreateCheckSuiteData): CheckSuite {
    const now = new Date().toISOString();
    const id = uuidv4();
    const checks = makeInitialChecks();

    this.db.run(
      `INSERT INTO check_suites (id, issue_id, snapshot_sha, status, checks, created_at, updated_at)
       VALUES (?, ?, ?, 'running', ?, ?, ?)`,
      [id, data.issueId, data.snapshotSha, JSON.stringify(checks), now, now]
    );

    return {
      id,
      issueId: data.issueId,
      snapshotSha: data.snapshotSha,
      status: 'running',
      checks,
      createdAt: now,
      updatedAt: now,
    };
  }

  findActiveByIssueId(issueId: string): CheckSuite | null {
    const row = this.db.get<CheckSuiteRow>(
      `SELECT * FROM check_suites
       WHERE issue_id = ? AND status IN ('running', 'awaiting-approval')
       ORDER BY created_at DESC LIMIT 1`,
      [issueId]
    );
    return row ? rowToCheckSuite(row) : null;
  }

  updateChecks(suiteId: string, checkName: string, checkState: CheckState): CheckSuite | null {
    const suite = this.findById(suiteId);
    if (!suite) return null;

    const checks = { ...suite.checks, [checkName]: checkState };
    const now = new Date().toISOString();

    this.db.run(
      'UPDATE check_suites SET checks = ?, updated_at = ? WHERE id = ?',
      [JSON.stringify(checks), now, suiteId]
    );

    return { ...suite, checks, updatedAt: now };
  }

  updateStatus(suiteId: string, status: CheckSuiteStatus): CheckSuite | null {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE check_suites SET status = ?, updated_at = ? WHERE id = ?',
      [status, now, suiteId]
    );
    return this.findById(suiteId);
  }

  updateSnapshotSha(suiteId: string, newSha: string): CheckSuite | null {
    const now = new Date().toISOString();
    const checks = makeInitialChecks();

    this.db.run(
      'UPDATE check_suites SET snapshot_sha = ?, checks = ?, status = ?, updated_at = ? WHERE id = ?',
      [newSha, JSON.stringify(checks), 'running', now, suiteId]
    );
    return this.findById(suiteId);
  }

  resetChecks(suiteId: string): CheckSuite | null {
    const now = new Date().toISOString();
    const checks = makeInitialChecks();

    this.db.run(
      'UPDATE check_suites SET checks = ?, status = ?, updated_at = ? WHERE id = ?',
      [JSON.stringify(checks), 'running', now, suiteId]
    );
    return this.findById(suiteId);
  }

  findById(suiteId: string): CheckSuite | null {
    const row = this.db.get<CheckSuiteRow>(
      'SELECT * FROM check_suites WHERE id = ?',
      [suiteId]
    );
    return row ? rowToCheckSuite(row) : null;
  }
}
