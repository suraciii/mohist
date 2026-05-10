import type { DatabaseManager } from '../db/database';
import { Stage } from '../types';

export type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
export type StageCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error';
export type StageStateStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';

export interface StageTaskState {
  taskId: string;
  title: string;
  status: StageTaskStatus;
  source: 'static' | 'dynamic';
  order: number;
  attempts: number;
  duration: number;
  artifacts: string[];
  output: unknown;
  startedAt: string | null;
  completedAt: string | null;
  updatedAt: string;
}

export interface StageCheckState {
  checkName: string;
  status: StageCheckStatus;
  message: string | null;
  output: unknown;
  runCount: number;
  lastRunAt: string | null;
  updatedAt: string;
}

export interface StageApprovalState {
  status: string;
  output: unknown;
  requestedAt: string | null;
  respondedAt: string | null;
}

export interface StageStateRead {
  stage: Stage;
  status: StageStateStatus;
  tasks: StageTaskState[];
  checks: StageCheckState[];
  approval: StageApprovalState | null;
  attempts: number;
  startedAt: string | null;
  completedAt: string | null;
  updatedAt: string;
}

interface StageTaskRow {
  issue_id: string;
  stage: string;
  task_id: string;
  title: string;
  status: string;
  source: string;
  task_order: number;
  attempts: number;
  duration: number;
  artifacts: string;
  output: string | null;
  started_at: string | null;
  completed_at: string | null;
  updated_at: string;
}

interface StageCheckRow {
  issue_id: string;
  stage: string;
  check_name: string;
  status: string;
  message: string | null;
  output: string | null;
  run_count: number;
  last_run_at: string | null;
  updated_at: string;
}

interface StageStateRow {
  issue_id: string;
  stage: string;
  status: string;
  started_at: string | null;
  completed_at: string | null;
  approval_status: string | null;
  approval_output: string | null;
  approval_requested_at: string | null;
  approval_responded_at: string | null;
  attempts: number;
  updated_at: string;
}

function rowToStageTask(row: StageTaskRow): StageTaskState {
  return {
    taskId: row.task_id,
    title: row.title,
    status: row.status as StageTaskStatus,
    source: row.source as 'static' | 'dynamic',
    order: row.task_order,
    attempts: row.attempts,
    duration: row.duration,
    artifacts: JSON.parse(row.artifacts || '[]'),
    output: row.output ? JSON.parse(row.output) : null,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    updatedAt: row.updated_at,
  };
}

function rowToStageCheck(row: StageCheckRow): StageCheckState {
  return {
    checkName: row.check_name,
    status: row.status as StageCheckStatus,
    message: row.message,
    output: row.output ? JSON.parse(row.output) : null,
    runCount: row.run_count,
    lastRunAt: row.last_run_at,
    updatedAt: row.updated_at,
  };
}

function rowToStageStateRead(
  row: StageStateRow,
  tasks: StageTaskState[],
  checks: StageCheckState[],
): StageStateRead {
  let approval: StageApprovalState | null = null;
  if (row.approval_status) {
    approval = {
      status: row.approval_status,
      output: row.approval_output ? JSON.parse(row.approval_output) : null,
      requestedAt: row.approval_requested_at,
      respondedAt: row.approval_responded_at,
    };
  }

  return {
    stage: row.stage as Stage,
    status: row.status as StageStateStatus,
    tasks,
    checks,
    approval,
    attempts: row.attempts,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    updatedAt: row.updated_at,
  };
}

export interface UpsertTaskInput {
  taskId: string;
  title: string;
  status: StageTaskStatus;
  source?: 'static' | 'dynamic';
  order?: number;
  attempts?: number;
  duration?: number;
  artifacts?: string[];
  output?: unknown;
  startedAt?: string | null;
  completedAt?: string | null;
}

export interface UpsertCheckInput {
  checkName: string;
  status: StageCheckStatus;
  message?: string | null;
  output?: unknown;
  runCount?: number;
  lastRunAt?: string | null;
}

export interface SetApprovalInput {
  status: string;
  output?: unknown;
  requestedAt?: string | null;
  respondedAt?: string | null;
}

export function normalizeCheckStatus(raw: string): StageCheckStatus {
  switch (raw) {
    case 'pass':
    case 'passed':
      return 'passed';
    case 'fail':
    case 'failed':
      return 'failed';
    case 'error':
      return 'error';
    case 'running':
      return 'running';
    default:
      return 'pending';
  }
}

export function normalizeTaskStatus(raw: string): StageTaskStatus {
  switch (raw) {
    case 'completed':
      return 'completed';
    case 'failed':
      return 'failed';
    case 'running':
      return 'running';
    case 'skipped':
      return 'skipped';
    default:
      return 'pending';
  }
}

interface StaticTaskDef {
  taskId: string;
  title: string;
  order: number;
}

const PLAN_TASK_DEFS: StaticTaskDef[] = [
  { taskId: 'read-context', title: 'Read context files', order: 1 },
  { taskId: 'design-solution', title: 'Design solution', order: 2 },
  { taskId: 'write-design', title: 'Write design document', order: 3 },
  { taskId: 'write-specs', title: 'Write specifications', order: 4 },
  { taskId: 'write-tasks', title: 'Write task breakdown', order: 5 },
];

const CHECK_TASK_DEFS: StaticTaskDef[] = [
  { taskId: 'build-test', title: 'Build & test', order: 1 },
  { taskId: 'ai-review', title: 'AI review', order: 2 },
  { taskId: 'user-approval', title: 'User approval', order: 3 },
];

const INTEGRATE_TASK_DEFS: StaticTaskDef[] = [
  { taskId: 'merge-branch', title: 'Merge branch', order: 1 },
  { taskId: 'verify-merge', title: 'Verify merge', order: 2 },
];

const STATIC_TASK_DEFS: Partial<Record<Stage, StaticTaskDef[]>> = {
  [Stage.Plan]: PLAN_TASK_DEFS,
  [Stage.Check]: CHECK_TASK_DEFS,
  [Stage.Integrate]: INTEGRATE_TASK_DEFS,
};

export class StageStateService {
  constructor(private db: DatabaseManager) {}

  setStageStatus(issueId: string, stage: Stage, status: StageStateStatus): void {
    const now = new Date().toISOString();
    const completedAt = (status === 'passed' || status === 'failed' || status === 'skipped') ? now : null;

    this.db.run(
      `UPDATE stage_states SET status = ?, completed_at = COALESCE(?, completed_at), updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [status, completedAt, now, issueId, stage],
    );
  }

  ensureStage(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.db.transaction(() => {
      const existing = this.db.get<StageStateRow>(
        'SELECT * FROM stage_states WHERE issue_id = ? AND stage = ?',
        [issueId, stage],
      );

      if (!existing) {
        this.db.run(
          `INSERT INTO stage_states (issue_id, stage, status, attempts, updated_at)
           VALUES (?, ?, 'pending', 0, ?)`,
          [issueId, stage, now],
        );
        this.seedStaticTasks(issueId, stage);
      } else {
        this.db.run(
          `UPDATE stage_states SET attempts = attempts + 1, status = 'running', started_at = ?, completed_at = NULL, updated_at = ?
           WHERE issue_id = ? AND stage = ?`,
          [now, now, issueId, stage],
        );
      }
    });
  }

  private seedStaticTasks(issueId: string, stage: Stage): void {
    const defs = STATIC_TASK_DEFS[stage];
    if (!defs) return;

    const now = new Date().toISOString();
    for (const def of defs) {
      this.db.run(
        `INSERT OR IGNORE INTO stage_tasks
         (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
         VALUES (?, ?, ?, ?, 'pending', 'static', ?, 0, 0, '[]', NULL, NULL, NULL, ?)`,
        [issueId, stage, def.taskId, def.title, def.order, now],
      );
    }
  }

  upsertTask(issueId: string, stage: Stage, input: UpsertTaskInput): void {
    const now = new Date().toISOString();
    const source = input.source ?? 'dynamic';
    const order = input.order ?? 0;
    const attempts = input.attempts ?? 1;
    const duration = input.duration ?? 0;
    const artifacts = JSON.stringify(input.artifacts ?? []);
    const output = input.output != null ? JSON.stringify(input.output) : null;
    const startedAt = input.startedAt ?? (input.status === 'running' ? now : null);
    const completedAt = input.completedAt ?? (input.status === 'completed' || input.status === 'failed' || input.status === 'skipped' ? now : null);

    this.ensureStageRowExists(issueId, stage);

    this.db.run(
      `INSERT INTO stage_tasks
        (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(issue_id, stage, task_id) DO UPDATE SET
         title = excluded.title,
         status = excluded.status,
         source = excluded.source,
         task_order = excluded.task_order,
         attempts = excluded.attempts,
         duration = excluded.duration,
         artifacts = excluded.artifacts,
         output = excluded.output,
         started_at = COALESCE(stage_tasks.started_at, excluded.started_at),
         completed_at = excluded.completed_at,
         updated_at = excluded.updated_at`,
      [issueId, stage, input.taskId, input.title, input.status, source, order, attempts, duration, artifacts, output, startedAt, completedAt, now],
    );
  }

  upsertCheck(issueId: string, stage: Stage, input: UpsertCheckInput): void {
    const now = new Date().toISOString();
    const message = input.message ?? null;
    const output = input.output != null ? JSON.stringify(input.output) : null;
    const runCount = input.runCount;
    const lastRunAt = input.lastRunAt ?? (input.status !== 'pending' ? now : null);

    this.ensureStageRowExists(issueId, stage);

    if (runCount != null) {
      this.db.run(
        `INSERT INTO stage_checks
          (issue_id, stage, check_name, status, message, output, run_count, last_run_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(issue_id, stage, check_name) DO UPDATE SET
           status = excluded.status,
           message = excluded.message,
           output = excluded.output,
           run_count = excluded.run_count,
           last_run_at = excluded.last_run_at,
           updated_at = excluded.updated_at`,
        [issueId, stage, input.checkName, input.status, message, output, runCount, lastRunAt, now],
      );
    } else {
      this.db.run(
        `INSERT INTO stage_checks
          (issue_id, stage, check_name, status, message, output, run_count, last_run_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?)
         ON CONFLICT(issue_id, stage, check_name) DO UPDATE SET
           status = excluded.status,
           message = excluded.message,
           output = excluded.output,
           run_count = stage_checks.run_count + 1,
           last_run_at = excluded.last_run_at,
           updated_at = excluded.updated_at`,
        [issueId, stage, input.checkName, input.status, message, output, lastRunAt, now],
      );
    }
  }

  setApproval(issueId: string, stage: Stage, input: SetApprovalInput): void {
    const now = new Date().toISOString();
    const approvalOutput = input.output != null ? JSON.stringify(input.output) : null;
    const requestedAt = input.requestedAt ?? null;
    const respondedAt = input.respondedAt ?? null;

    this.ensureStageRowExists(issueId, stage);

    this.db.run(
      `UPDATE stage_states SET
         approval_status = ?,
         approval_output = ?,
         approval_requested_at = ?,
         approval_responded_at = ?,
         updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [input.status, approvalOutput, requestedAt, respondedAt, now, issueId, stage],
    );
  }

  getStageState(issueId: string, stage: Stage): StageStateRead | null {
    const stateRow = this.db.get<StageStateRow>(
      'SELECT * FROM stage_states WHERE issue_id = ? AND stage = ?',
      [issueId, stage],
    );
    if (!stateRow) return null;

    const taskRows = this.db.all<StageTaskRow>(
      'SELECT * FROM stage_tasks WHERE issue_id = ? AND stage = ? ORDER BY task_order ASC, task_id ASC',
      [issueId, stage],
    );
    const checkRows = this.db.all<StageCheckRow>(
      'SELECT * FROM stage_checks WHERE issue_id = ? AND stage = ? ORDER BY check_name ASC',
      [issueId, stage],
    );

    return rowToStageStateRead(
      stateRow,
      taskRows.map(rowToStageTask),
      checkRows.map(rowToStageCheck),
    );
  }

  getIssueStageState(issueId: string): StageStateRead[] {
    const stateRows = this.db.all<StageStateRow>(
      'SELECT * FROM stage_states WHERE issue_id = ? ORDER BY stage ASC',
      [issueId],
    );

    return stateRows.map(row => {
      const taskRows = this.db.all<StageTaskRow>(
        'SELECT * FROM stage_tasks WHERE issue_id = ? AND stage = ? ORDER BY task_order ASC, task_id ASC',
        [issueId, row.stage],
      );
      const checkRows = this.db.all<StageCheckRow>(
        'SELECT * FROM stage_checks WHERE issue_id = ? AND stage = ? ORDER BY check_name ASC',
        [issueId, row.stage],
      );
      return rowToStageStateRead(
        row,
        taskRows.map(rowToStageTask),
        checkRows.map(rowToStageCheck),
      );
    });
  }

  private ensureStageRowExists(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.db.run(
      `INSERT OR IGNORE INTO stage_states (issue_id, stage, status, attempts, updated_at)
       VALUES (?, ?, 'pending', 0, ?)`,
      [issueId, stage, now],
    );
  }
}
