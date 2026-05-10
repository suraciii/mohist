import * as fs from 'fs';
import * as path from 'path';
import type { DatabaseManager } from '../db/database';
import { findChangeDir } from '../openspec/detector';
import { Stage } from '../types';
import type { CheckState, CheckSuiteChecks } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import type { CheckResult, StageTaskResult } from '../workflow/stage-context';

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

interface IssueLookupRow {
  id: string;
  number: number;
  project_id: string;
  stage: string;
  approval_state: string | null;
}

interface ProjectLookupRow {
  id: string;
  path: string;
}

interface StageExecutionProjectionRow {
  issue_id: string;
  stage: string;
  status: string;
  task_results: string;
  check_results: string;
  created_at: string;
  updated_at: string;
}

interface CheckSuiteProjectionRow {
  issue_id: string;
  status: string;
  checks: string;
  updated_at: string;
}

interface LegacyApprovalState {
  stage?: Stage;
  status: string;
  output?: unknown;
  requestedAt?: string;
  respondedAt?: string;
}

interface ProjectedStageSeed {
  status: StageStateStatus;
  startedAt: string | null;
  completedAt: string | null;
  attempts: number;
  updatedAt: string;
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

function normalizeStageStateStatus(raw: string): StageStateStatus {
  switch (raw) {
    case 'running':
      return 'running';
    case 'awaiting-approval':
      return 'awaiting-approval';
    case 'passed':
      return 'passed';
    case 'failed':
      return 'failed';
    case 'skipped':
      return 'skipped';
    default:
      return 'pending';
  }
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
          `INSERT INTO stage_states (issue_id, stage, status, attempts, started_at, updated_at)
           VALUES (?, ?, 'running', 0, ?, ?)`,
          [issueId, stage, now, now],
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

  clearApproval(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.ensureStageRowExists(issueId, stage);
    this.db.run(
      `UPDATE stage_states SET
         approval_status = NULL,
         approval_output = NULL,
         approval_requested_at = NULL,
         approval_responded_at = NULL,
         updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [now, issueId, stage],
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
    this.projectLegacyStageState(issueId);

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

  private projectLegacyStageState(issueId: string): void {
    const issue = this.db.get<IssueLookupRow>(
      'SELECT id, number, project_id, stage, approval_state FROM issues WHERE id = ?',
      [issueId],
    );
    if (!issue) return;

    const stageRows = this.db.all<{ stage: string }>(
      'SELECT stage FROM stage_states WHERE issue_id = ?',
      [issueId],
    );
    const persistedStages = new Set(stageRows.map(row => row.stage as Stage));

    const executionRows = this.db.all<StageExecutionProjectionRow>(
      `SELECT issue_id, stage, status, task_results, check_results, created_at, updated_at
       FROM stage_executions
       WHERE issue_id = ?
       ORDER BY created_at ASC`,
      [issueId],
    );
    const suiteRow = this.db.get<CheckSuiteProjectionRow>(
      `SELECT issue_id, status, checks, updated_at
       FROM check_suites
       WHERE issue_id = ? AND status IN ('running', 'awaiting-approval')
       ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    const tasksFile = this.readLegacyTasksFile(issue.number, issue.project_id);

    const hasLegacyEvidence = executionRows.length > 0 || suiteRow != null || tasksFile != null;
    if (!hasLegacyEvidence) return;

    const stagesToSeed = new Set<Stage>();
    for (const row of executionRows) {
      stagesToSeed.add(row.stage as Stage);
    }
    if (suiteRow) stagesToSeed.add(Stage.Check);
    if (tasksFile?.tasks.length) {
      stagesToSeed.add(Stage.Build);
    }

    const approvalState = this.parseJson<LegacyApprovalState | null>(issue.approval_state, null);
    if (approvalState?.stage) {
      stagesToSeed.add(approvalState.stage);
    }

    for (const stage of stagesToSeed) {
      if (persistedStages.has(stage)) continue;
      this.seedProjectedStageState(issueId, stage, executionRows, suiteRow, tasksFile, approvalState, issue.stage as Stage);
    }
  }

  private seedProjectedStageState(
    issueId: string,
    stage: Stage,
    executionRows: StageExecutionProjectionRow[],
    suiteRow: CheckSuiteProjectionRow | undefined,
    tasksFile: TasksFile | null,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): void {
    const stageExecutions = executionRows.filter(row => row.stage === stage);
    const projected = this.buildProjectedStageSeed(stage, stageExecutions, suiteRow, approvalState, issueStage);
    if (!projected) return;

    this.db.transaction(() => {
      this.insertProjectedStageState(issueId, stage, projected, approvalState);
      this.seedStaticTasks(issueId, stage);

      this.projectLegacyBuildTasks(issueId, stage, tasksFile);
      this.projectExecutionEvidence(issueId, stage, stageExecutions);
      this.projectSuiteChecks(issueId, stage, suiteRow);
    });
  }

  private insertProjectedStageState(
    issueId: string,
    stage: Stage,
    projected: ProjectedStageSeed,
    approvalState: LegacyApprovalState | null,
  ): void {
    this.db.run(
      `INSERT OR IGNORE INTO stage_states
       (issue_id, stage, status, started_at, completed_at, approval_status, approval_output, approval_requested_at, approval_responded_at, attempts, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        issueId,
        stage,
        projected.status,
        projected.startedAt,
        projected.completedAt,
        approvalState?.stage === stage ? approvalState.status : null,
        approvalState?.stage === stage && approvalState.output != null ? JSON.stringify(approvalState.output) : null,
        approvalState?.stage === stage ? approvalState.requestedAt ?? null : null,
        approvalState?.stage === stage ? approvalState.respondedAt ?? null : null,
        projected.attempts,
        projected.updatedAt,
      ],
    );
  }

  private projectLegacyBuildTasks(issueId: string, stage: Stage, tasksFile: TasksFile | null): void {
    if (stage !== Stage.Build || !tasksFile) return;

    for (const task of tasksFile.tasks) {
      const status = task.passes
        ? 'completed'
        : task.error
          ? 'failed'
          : 'pending';
      this.upsertTask(issueId, stage, {
        taskId: task.id,
        title: task.title,
        status,
        source: 'dynamic',
        order: task.order,
        attempts: task.attempts,
        output: task.error ? { error: task.error } : undefined,
      });
    }
  }

  private projectExecutionEvidence(
    issueId: string,
    stage: Stage,
    stageExecutions: StageExecutionProjectionRow[],
  ): void {
    for (const execution of stageExecutions) {
      this.projectExecutionTaskEvidence(issueId, stage, execution);
      this.projectExecutionCheckEvidence(issueId, stage, execution);
    }
  }

  private projectExecutionTaskEvidence(
    issueId: string,
    stage: Stage,
    execution: StageExecutionProjectionRow,
  ): void {
    const taskResults = this.parseJson<StageTaskResult[]>(execution.task_results, []);
    for (const result of taskResults) {
      this.upsertTask(issueId, stage, {
        taskId: result.taskId,
        title: result.title,
        status: result.status,
        artifacts: result.artifacts,
        output: result.output,
        attempts: result.attempts,
        duration: result.duration,
      });
    }
  }

  private projectExecutionCheckEvidence(
    issueId: string,
    stage: Stage,
    execution: StageExecutionProjectionRow,
  ): void {
    const checkResults = this.parseJson<CheckResult[]>(execution.check_results, []);
    for (const result of checkResults) {
      this.upsertCheck(issueId, stage, {
        checkName: result.name,
        status: normalizeCheckStatus(result.status),
        message: result.message ?? null,
        output: result.output,
      });
    }
  }

  private projectSuiteChecks(
    issueId: string,
    stage: Stage,
    suiteRow: CheckSuiteProjectionRow | undefined,
  ): void {
    if (stage !== Stage.Check || !suiteRow) return;

    const checks = this.parseJson<CheckSuiteChecks | null>(suiteRow.checks, null);
    if (!checks) return;

    for (const [checkName, state] of Object.entries(checks)) {
      this.upsertCheck(issueId, stage, {
        checkName,
        status: this.normalizeProjectedCheckSuiteStatus(state),
        output: state.output,
        lastRunAt: state.ranAt ?? suiteRow.updated_at,
      });
    }
  }

  private buildProjectedStageSeed(
    stage: Stage,
    stageExecutions: StageExecutionProjectionRow[],
    suiteRow: CheckSuiteProjectionRow | undefined,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): ProjectedStageSeed | null {
    const latestExecution = stageExecutions.at(-1);
    const status = this.resolveProjectedStageStatus(stage, latestExecution, suiteRow, approvalState, issueStage);
    const completedAt = status === 'passed' || status === 'failed' || status === 'skipped'
      ? latestExecution?.updated_at ?? null
      : null;
    const startedAt = this.resolveProjectedStageStartedAt(status, latestExecution, suiteRow);
    const updatedAt = suiteRow?.updated_at ?? latestExecution?.updated_at ?? new Date().toISOString();

    if (!latestExecution && !suiteRow && approvalState?.stage !== stage && status === 'pending') {
      return null;
    }

    return {
      status,
      startedAt,
      completedAt,
      attempts: Math.max(0, stageExecutions.length - 1),
      updatedAt,
    };
  }

  private resolveProjectedStageStatus(
    stage: Stage,
    latestExecution: StageExecutionProjectionRow | undefined,
    suiteRow: CheckSuiteProjectionRow | undefined,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): StageStateStatus {
    const executionStatus = latestExecution ? normalizeStageStateStatus(latestExecution.status) : null;
    const suiteStatus = stage === Stage.Check && suiteRow ? normalizeStageStateStatus(suiteRow.status) : null;
    const approvalStatus = approvalState?.stage === stage && approvalState.status === 'awaiting'
      ? 'awaiting-approval'
      : null;
    const liveStatus = issueStage === stage ? 'running' : null;

    return approvalStatus ?? suiteStatus ?? liveStatus ?? executionStatus ?? 'pending';
  }

  private resolveProjectedStageStartedAt(
    status: StageStateStatus,
    latestExecution: StageExecutionProjectionRow | undefined,
    suiteRow: CheckSuiteProjectionRow | undefined,
  ): string | null {
    if (latestExecution) return latestExecution.created_at;
    if (status === 'running' || status === 'awaiting-approval') {
      return suiteRow?.updated_at ?? null;
    }
    return null;
  }

  private normalizeProjectedCheckSuiteStatus(state: CheckState): StageCheckStatus {
    return normalizeCheckStatus(state.status);
  }

  private readLegacyTasksFile(issueNumber: number, projectId: string): TasksFile | null {
    const project = this.db.get<ProjectLookupRow>(
      'SELECT id, path FROM projects WHERE id = ?',
      [projectId],
    );
    if (!project) return null;

    const changeDir = findChangeDir(project.path, issueNumber);
    if (!changeDir) return null;

    const tasksPath = path.join(changeDir, 'tasks.json');
    if (!fs.existsSync(tasksPath)) return null;

    try {
      const parsed = JSON.parse(fs.readFileSync(tasksPath, 'utf-8')) as TasksFile;
      return Array.isArray(parsed.tasks) ? parsed : null;
    } catch {
      return null;
    }
  }

  private parseJson<T>(value: string | null, fallback: T): T {
    if (!value) return fallback;
    try {
      return JSON.parse(value) as T;
    } catch {
      return fallback;
    }
  }
}
