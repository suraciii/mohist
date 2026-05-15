import { DatabaseManager } from './database';
import { Stage } from '../types';
import {
  DEFAULT_STAGE_DEFINITIONS,
  type CausedByMetadata,
  type StageRunSnapshot,
  type WorkflowRunSnapshot,
  WorkflowRun as DomainWorkflowRun,
} from '../workflow/domain';
import {
  freezePointFromStageSnapshot,
  hydrateWorkflowRun,
  repairWorkflowRunSnapshot,
} from '../workflow/domain/persistence';
import fs from 'fs';

export type WorkflowRunStatus = 'running' | 'passed' | 'failed' | 'cancelled';
export type WorkflowTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
export type WorkflowCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error';
export type WorkflowStageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';

export interface WorkflowRun {
  id: string;
  issueId: string;
  issueNumber: number;
  status: WorkflowRunStatus;
  currentStage: Stage;
  startedBy: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowStageRun {
  id: string;
  workflowRunId: string;
  stage: Stage;
  status: WorkflowStageRunStatus;
  stageOrder: number;
  approvalStatus: string | null;
  approvalOutput: unknown | null;
  approvalRequestedAt: string | null;
  approvalRespondedAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  updatedAt: string;
  staleEvidenceDetected?: boolean;
}

export interface WorkflowTask {
  id: string;
  workflowRunId: string;
  stageRunId: string;
  taskId: string;
  title: string;
  status: WorkflowTaskStatus;
  taskOrder: number;
  attempts: number;
  duration: number;
  artifacts: string[];
  output: unknown | null;
  reason: string | null;
  causedByType: string | null;
  causedByCheckName: string | null;
  causedByTaskId: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowCheck {
  id: string;
  workflowRunId: string;
  stageRunId: string;
  checkName: string;
  title: string;
  status: WorkflowCheckStatus;
  message: string | null;
  output: unknown | null;
  runCount: number;
  lastRunAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowRunWithStageRuns extends WorkflowRun {
  stageRuns: WorkflowStageRunWithTasksAndChecks[];
}

export interface WorkflowStageRunWithTasksAndChecks extends WorkflowStageRun {
  tasks: WorkflowTask[];
  checks: WorkflowCheck[];
}

interface WorkflowRunRow {
  id: string;
  issue_id: string;
  issue_number: number;
  status: string;
  current_stage: string;
  started_by: string | null;
  created_at: string;
  updated_at: string;
}

interface WorkflowStageRunRow {
  id: string;
  workflow_run_id: string;
  stage: string;
  status: string;
  stage_order: number;
  approval_status: string | null;
  approval_output: string | null;
  approval_requested_at: string | null;
  approval_responded_at: string | null;
  started_at: string | null;
  completed_at: string | null;
  created_at: string;
  updated_at: string;
}

interface WorkflowTaskRow {
  id: string;
  workflow_run_id: string;
  stage_run_id: string;
  task_id: string;
  title: string;
  status: string;
  task_order: number;
  attempts: number;
  duration: number;
  artifacts: string;
  output: string | null;
  reason: string | null;
  caused_by_type: string | null;
  caused_by_check_name: string | null;
  caused_by_task_id: string | null;
  started_at: string | null;
  completed_at: string | null;
  created_at: string;
  updated_at: string;
}

interface WorkflowCheckRow {
  id: string;
  workflow_run_id: string;
  stage_run_id: string;
  check_name: string;
  title: string;
  status: string;
  message: string | null;
  output: string | null;
  run_count: number;
  last_run_at: string | null;
  created_at: string;
  updated_at: string;
}

function rowToWorkflowRun(row: WorkflowRunRow): WorkflowRun {
  return {
    id: row.id,
    issueId: row.issue_id,
    issueNumber: row.issue_number,
    status: row.status as WorkflowRunStatus,
    currentStage: row.current_stage as Stage,
    startedBy: row.started_by,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function rowToWorkflowStageRun(row: WorkflowStageRunRow): WorkflowStageRun {
  return {
    id: row.id,
    workflowRunId: row.workflow_run_id,
    stage: row.stage as Stage,
    status: row.status as WorkflowStageRunStatus,
    stageOrder: row.stage_order,
    approvalStatus: row.approval_status,
    approvalOutput: row.approval_output ? JSON.parse(row.approval_output) : null,
    approvalRequestedAt: row.approval_requested_at,
    approvalRespondedAt: row.approval_responded_at,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function rowToWorkflowTask(row: WorkflowTaskRow): WorkflowTask {
  return {
    id: row.id,
    workflowRunId: row.workflow_run_id,
    stageRunId: row.stage_run_id,
    taskId: row.task_id,
    title: row.title,
    status: row.status as WorkflowTaskStatus,
    taskOrder: row.task_order,
    attempts: row.attempts,
    duration: row.duration,
    artifacts: JSON.parse(row.artifacts || '[]'),
    output: row.output ? JSON.parse(row.output) : null,
    reason: row.reason,
    causedByType: row.caused_by_type,
    causedByCheckName: row.caused_by_check_name,
    causedByTaskId: row.caused_by_task_id,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function rowToWorkflowCheck(row: WorkflowCheckRow): WorkflowCheck {
  return {
    id: row.id,
    workflowRunId: row.workflow_run_id,
    stageRunId: row.stage_run_id,
    checkName: row.check_name,
    title: row.title,
    status: row.status as WorkflowCheckStatus,
    message: row.message,
    output: row.output ? JSON.parse(row.output) : null,
    runCount: row.run_count,
    lastRunAt: row.last_run_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function safeParseJson(value: string | null): unknown | null {
  if (!value) return null;
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function causedByFromTask(row: WorkflowTaskRow): CausedByMetadata | null {
  if (!row.caused_by_type) return null;
  return {
    type: row.caused_by_type as CausedByMetadata['type'],
    checkName: row.caused_by_check_name ?? undefined,
    taskId: row.caused_by_task_id ?? undefined,
    message: row.reason ?? undefined,
  };
}

function taskRowToSnapshot(row: WorkflowTaskRow): StageRunSnapshot['tasks'][number] {
  return {
    id: row.task_id,
    title: row.title,
    status: row.status as WorkflowTaskStatus,
    order: row.task_order,
    dependsOn: [],
    attempts: row.attempts,
    duration: row.duration,
    artifacts: JSON.parse(row.artifacts || '[]'),
    output: safeParseJson(row.output),
    reason: row.reason,
    causedBy: causedByFromTask(row),
  };
}

function checkRowToSnapshot(row: WorkflowCheckRow): StageRunSnapshot['checks'][number] {
  return {
    name: row.check_name,
    title: row.title,
    status: row.status as WorkflowCheckStatus,
    message: row.message,
    output: safeParseJson(row.output),
    runCount: row.run_count,
  };
}

function approvalFromStageRow(row: WorkflowStageRunRow): StageRunSnapshot['approval'] {
  if (!row.approval_status || !row.approval_requested_at) return null;
  return {
    status: row.approval_status as 'awaiting' | 'approved' | 'rejected',
    output: safeParseJson(row.approval_output),
    requestedAt: row.approval_requested_at,
    respondedAt: row.approval_responded_at,
  };
}

function readBuildTasks(tasksPath?: string): Array<{ id: string; title: string; order?: number; dependsOn?: string[] }> {
  if (!tasksPath || !fs.existsSync(tasksPath)) return [];
  try {
    const parsed = JSON.parse(fs.readFileSync(tasksPath, 'utf-8')) as {
      tasks?: Array<{ id?: unknown; title?: unknown; order?: unknown; dependsOn?: unknown }>;
    };
    if (!Array.isArray(parsed.tasks)) return [];
    return parsed.tasks.flatMap((task, index) => {
      if (typeof task.id !== 'string') return [];
      return [{
        id: task.id,
        title: typeof task.title === 'string' ? task.title : task.id,
        order: typeof task.order === 'number' ? task.order : index,
        dependsOn: Array.isArray(task.dependsOn)
          ? task.dependsOn.filter((dep): dep is string => typeof dep === 'string')
          : [],
      }];
    });
  } catch {
    return [];
  }
}

function orderCheckSnapshots(stage: Stage, checks: StageRunSnapshot['checks']): StageRunSnapshot['checks'] {
  const definition = DEFAULT_STAGE_DEFINITIONS.find(candidate => candidate.stage === stage);
  if (!definition) return checks;
  const order = new Map(definition.checks.map((check, index) => [check.name, index]));
  return [...checks].sort((a, b) => (order.get(a.name) ?? Number.MAX_SAFE_INTEGER) - (order.get(b.name) ?? Number.MAX_SAFE_INTEGER) || a.name.localeCompare(b.name));
}

export class WorkflowRunRepo {
  constructor(private db: DatabaseManager) {}

  createOrLoadActiveAggregate(data: {
    issueId: string;
    issueNumber: number;
    startedBy?: string | null;
    tasksPath?: string;
  }): DomainWorkflowRun {
    return this.db.transaction(() => {
      const existing = this.loadRunningAggregate(data.issueId, { tasksPath: data.tasksPath });
      if (existing) return existing;

      const id = `wr_${data.issueNumber}_${Date.now()}`;
      const { run } = DomainWorkflowRun.startWorkflow({
        id,
        issueId: data.issueId,
        issueNumber: data.issueNumber,
      });
      this.saveAggregate(run, data.startedBy ?? null);
      return this.loadRunningAggregate(data.issueId, { tasksPath: data.tasksPath }) ?? run;
    });
  }

  loadActiveAggregate(issueId: string, options: { tasksPath?: string } = {}): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status != 'cancelled' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row, options);
  }

  loadRunningAggregate(issueId: string, options: { tasksPath?: string } = {}): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status = 'running' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row, options);
  }

  loadLatestAggregate(issueId: string, options: { tasksPath?: string } = {}): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status != 'cancelled' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row, options);
  }

  loadAggregateById(id: string, options: { tasksPath?: string } = {}): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [id]);
    if (!row) return null;
    return this.loadAggregateByRow(row, options);
  }

  saveAggregate(run: DomainWorkflowRun, startedBy?: string | null): void {
    const snapshot = run.snapshot();
    this.db.transaction(() => {
      this.saveAggregateSnapshot(snapshot, startedBy);
    });
  }

  private loadAggregateByRow(row: WorkflowRunRow, options: { tasksPath?: string }): DomainWorkflowRun {
    return this.db.transaction(() => {
      const repaired = repairWorkflowRunSnapshot(
        this.snapshotFromRows(row),
        readBuildTasks(options.tasksPath),
      );
      this.saveAggregateSnapshot(repaired, row.started_by);
      const freshRow = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [row.id]) ?? row;
      return hydrateWorkflowRun(this.snapshotFromRows(freshRow));
    });
  }

  private snapshotFromRows(row: WorkflowRunRow): WorkflowRunSnapshot {
    const stageRows = this.db.all<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? ORDER BY stage_order ASC',
      [row.id],
    );

    const stageRuns = stageRows.map((stageRow): StageRunSnapshot => {
      const taskRows = this.db.all<WorkflowTaskRow>(
        'SELECT * FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC',
        [stageRow.id],
      );
      const checkRows = this.db.all<WorkflowCheckRow>(
        'SELECT * FROM workflow_checks WHERE stage_run_id = ? ORDER BY check_name ASC',
        [stageRow.id],
      );
      const stageSnapshot: StageRunSnapshot = {
        stage: stageRow.stage as Stage,
        status: stageRow.status as WorkflowStageRunStatus,
        order: stageRow.stage_order,
        tasks: taskRows.map(taskRowToSnapshot),
        checks: orderCheckSnapshots(stageRow.stage as Stage, checkRows.map(checkRowToSnapshot)),
        approval: approvalFromStageRow(stageRow),
        failure: null,
        freezePoint: null,
      };
      stageSnapshot.freezePoint = freezePointFromStageSnapshot(stageSnapshot.stage, stageSnapshot);
      return stageSnapshot;
    });

    const failureStage = stageRuns.find(stage => stage.status === 'failed');
    const snapshot: WorkflowRunSnapshot = {
      id: row.id,
      issueId: row.issue_id,
      issueNumber: row.issue_number,
      status: row.status as WorkflowRunStatus,
      currentStage: row.current_stage as Stage,
      stageOrder: DEFAULT_STAGE_DEFINITIONS.map(definition => definition.stage),
      stageRuns,
      failure: failureStage?.failure ?? null,
    };
    return snapshot;
  }

  private saveAggregateSnapshot(snapshot: WorkflowRunSnapshot, startedBy?: string | null): void {
    const now = new Date().toISOString();
    const existingRun = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [snapshot.id]);
    if (existingRun) {
      this.db.run(
        `UPDATE workflow_runs SET issue_id = ?, issue_number = ?, status = ?, current_stage = ?, started_by = ?, updated_at = ? WHERE id = ?`,
        [snapshot.issueId, snapshot.issueNumber, snapshot.status, snapshot.currentStage, startedBy ?? existingRun.started_by, now, snapshot.id],
      );
    } else {
      this.db.run(
        `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, started_by, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        [snapshot.id, snapshot.issueId, snapshot.issueNumber, snapshot.status, snapshot.currentStage, startedBy ?? null, now, now],
      );
    }

    for (const stageRun of snapshot.stageRuns) {
      const stageRunId = `${snapshot.id}/${stageRun.stage}`;
      const existingStage = this.db.get<WorkflowStageRunRow>('SELECT * FROM workflow_stage_runs WHERE id = ?', [stageRunId]);
      const startedAt = stageRun.status === 'running' && !existingStage?.started_at ? now : existingStage?.started_at ?? null;
      const completedAt = stageRun.status === 'passed' || stageRun.status === 'failed'
        ? existingStage?.completed_at ?? now
        : null;
      const approval = stageRun.approval;

      if (existingStage) {
        this.db.run(
          `UPDATE workflow_stage_runs
           SET status = ?, stage_order = ?, approval_status = ?, approval_output = ?, approval_requested_at = ?,
               approval_responded_at = ?, started_at = ?, completed_at = ?, updated_at = ?
           WHERE id = ?`,
          [
            stageRun.status,
            stageRun.order,
            approval?.status ?? null,
            approval ? JSON.stringify(approval.output) : null,
            approval?.requestedAt ?? null,
            approval?.respondedAt ?? null,
            startedAt,
            completedAt,
            now,
            stageRunId,
          ],
        );
      } else {
        this.db.run(
          `INSERT INTO workflow_stage_runs
           (id, workflow_run_id, stage, status, stage_order, approval_status, approval_output,
            approval_requested_at, approval_responded_at, started_at, completed_at, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          [
            stageRunId,
            snapshot.id,
            stageRun.stage,
            stageRun.status,
            stageRun.order,
            approval?.status ?? null,
            approval ? JSON.stringify(approval.output) : null,
            approval?.requestedAt ?? null,
            approval?.respondedAt ?? null,
            startedAt,
            completedAt,
            now,
            now,
          ],
        );
      }

      for (const task of stageRun.tasks) {
        this.upsertTaskInternal({
          workflowRunId: snapshot.id,
          stageRunId,
          taskId: task.id,
          title: task.title,
          status: task.status,
          taskOrder: task.order,
          attempts: task.attempts,
          duration: task.duration,
          artifacts: task.artifacts,
          output: task.output,
          reason: task.reason,
          causedByType: task.causedBy?.type ?? null,
          causedByCheckName: task.causedBy?.checkName ?? null,
          causedByTaskId: task.causedBy?.taskId ?? null,
        });
      }

      for (const check of stageRun.checks) {
        this.upsertCheckInternal({
          workflowRunId: snapshot.id,
          stageRunId,
          checkName: check.name,
          title: check.title,
          status: check.status,
          message: check.message,
          output: check.output,
          runCount: check.runCount,
        });
      }
    }
  }

  create(data: {
    issueId: string;
    issueNumber: number;
    startedBy?: string | null;
  }): WorkflowRun {
    const now = new Date().toISOString();
    const id = `wr_${data.issueNumber}_${Date.now()}`;

    this.db.run(
      `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, started_by, created_at, updated_at)
       VALUES (?, ?, ?, 'running', 'plan', ?, ?, ?)`,
      [id, data.issueId, data.issueNumber, data.startedBy ?? null, now, now],
    );

    return {
      id,
      issueId: data.issueId,
      issueNumber: data.issueNumber,
      status: 'running',
      currentStage: Stage.Plan,
      startedBy: data.startedBy ?? null,
      createdAt: now,
      updatedAt: now,
    };
  }

  findById(id: string): WorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      'SELECT * FROM workflow_runs WHERE id = ?',
      [id],
    );
    return row ? rowToWorkflowRun(row) : null;
  }

  findActiveByIssueId(issueId: string): WorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status = 'running' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    return row ? rowToWorkflowRun(row) : null;
  }

  findByIssueId(issueId: string): WorkflowRun[] {
    const rows = this.db.all<WorkflowRunRow>(
      'SELECT * FROM workflow_runs WHERE issue_id = ? ORDER BY created_at DESC',
      [issueId],
    );
    return rows.map(rowToWorkflowRun);
  }

  getActiveRunWithRelations(issueId: string): WorkflowRunWithStageRuns | null {
    const runRow = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status != 'cancelled' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!runRow) return null;

    const run = rowToWorkflowRun(runRow);

    const stageRunRows = this.db.all<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? ORDER BY stage_order ASC',
      [run.id],
    );

    const stageRuns: WorkflowStageRunWithTasksAndChecks[] = stageRunRows.map(srRow => {
      const stageRun = rowToWorkflowStageRun(srRow);

      const taskRows = this.db.all<WorkflowTaskRow>(
        'SELECT * FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC',
        [stageRun.id],
      );

      const checkRows = this.db.all<WorkflowCheckRow>(
        'SELECT * FROM workflow_checks WHERE stage_run_id = ? ORDER BY check_name ASC',
        [stageRun.id],
      );

      return {
        ...stageRun,
        tasks: taskRows.map(rowToWorkflowTask),
        checks: checkRows.map(rowToWorkflowCheck),
      };
    });

    return {
      ...run,
      stageRuns,
    };
  }

  getLatestRunWithRelations(issueId: string, options: { includeCancelled?: boolean } = {}): WorkflowRunWithStageRuns | null {
    const statusClause = options.includeCancelled ? '' : ` AND status != 'cancelled'`;
    const runRow = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ?${statusClause} ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!runRow) return null;

    const run = rowToWorkflowRun(runRow);

    const stageRunRows = this.db.all<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? ORDER BY stage_order ASC',
      [run.id],
    );

    const stageRuns: WorkflowStageRunWithTasksAndChecks[] = stageRunRows.map(srRow => {
      const stageRun = rowToWorkflowStageRun(srRow);
      const taskRows = this.db.all<WorkflowTaskRow>(
        'SELECT * FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC',
        [stageRun.id],
      );
      const checkRows = this.db.all<WorkflowCheckRow>(
        'SELECT * FROM workflow_checks WHERE stage_run_id = ? ORDER BY check_name ASC',
        [stageRun.id],
      );
      return {
        ...stageRun,
        tasks: taskRows.map(rowToWorkflowTask),
        checks: checkRows.map(rowToWorkflowCheck),
      };
    });

    return { ...run, stageRuns };
  }

  createStageRun(data: {
    workflowRunId: string;
    stage: Stage;
    stageOrder: number;
  }): WorkflowStageRun {
    const now = new Date().toISOString();
    const id = `${data.workflowRunId}/${data.stage}`;

    this.db.run(
      `INSERT INTO workflow_stage_runs (id, workflow_run_id, stage, status, stage_order, created_at, updated_at)
       VALUES (?, ?, ?, 'pending', ?, ?, ?)`,
      [id, data.workflowRunId, data.stage, data.stageOrder, now, now],
    );

    return {
      id,
      workflowRunId: data.workflowRunId,
      stage: data.stage,
      status: 'pending',
      stageOrder: data.stageOrder,
      approvalStatus: null,
      approvalOutput: null,
      approvalRequestedAt: null,
      approvalRespondedAt: null,
      startedAt: null,
      completedAt: null,
      createdAt: now,
      updatedAt: now,
    };
  }

  findStageRunById(id: string): WorkflowStageRun | null {
    const row = this.db.get<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE id = ?',
      [id],
    );
    return row ? rowToWorkflowStageRun(row) : null;
  }

  createTask(data: {
    workflowRunId: string;
    stageRunId: string;
    taskId: string;
    title: string;
    taskOrder?: number;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
    causedByTaskId?: string | null;
  }): WorkflowTask {
    const now = new Date().toISOString();
    const id = `${data.stageRunId}/${data.taskId}`;

    this.db.run(
      `INSERT INTO workflow_tasks (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, artifacts, created_at, updated_at, reason, caused_by_type, caused_by_check_name, caused_by_task_id)
       VALUES (?, ?, ?, ?, ?, 'pending', ?, '[]', ?, ?, ?, ?, ?, ?)`,
      [id, data.workflowRunId, data.stageRunId, data.taskId, data.title, data.taskOrder ?? 0, now, now, data.reason ?? null, data.causedByType ?? null, data.causedByCheckName ?? null, data.causedByTaskId ?? null],
    );

    return {
      id,
      workflowRunId: data.workflowRunId,
      stageRunId: data.stageRunId,
      taskId: data.taskId,
      title: data.title,
      status: 'pending',
      taskOrder: data.taskOrder ?? 0,
      attempts: 0,
      duration: 0,
      artifacts: [],
      output: null,
      reason: data.reason ?? null,
      causedByType: data.causedByType ?? null,
      causedByCheckName: data.causedByCheckName ?? null,
      causedByTaskId: data.causedByTaskId ?? null,
      startedAt: null,
      completedAt: null,
      createdAt: now,
      updatedAt: now,
    };
  }

  findTaskById(id: string): WorkflowTask | null {
    const row = this.db.get<WorkflowTaskRow>(
      'SELECT * FROM workflow_tasks WHERE id = ?',
      [id],
    );
    return row ? rowToWorkflowTask(row) : null;
  }

  createCheck(data: {
    workflowRunId: string;
    stageRunId: string;
    checkName: string;
    title: string;
  }): WorkflowCheck {
    const now = new Date().toISOString();
    const id = `${data.stageRunId}/${data.checkName}`;

    this.db.run(
      `INSERT INTO workflow_checks (id, workflow_run_id, stage_run_id, check_name, title, status, run_count, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, 'pending', 0, ?, ?)`,
      [id, data.workflowRunId, data.stageRunId, data.checkName, data.title, now, now],
    );

    return {
      id,
      workflowRunId: data.workflowRunId,
      stageRunId: data.stageRunId,
      checkName: data.checkName,
      title: data.title,
      status: 'pending',
      message: null,
      output: null,
      runCount: 0,
      lastRunAt: null,
      createdAt: now,
      updatedAt: now,
    };
  }

  findCheckById(id: string): WorkflowCheck | null {
    const row = this.db.get<WorkflowCheckRow>(
      'SELECT * FROM workflow_checks WHERE id = ?',
      [id],
    );
    return row ? rowToWorkflowCheck(row) : null;
  }

  findStageRunByStage(runId: string, stage: Stage): WorkflowStageRun | null {
    const row = this.db.get<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? AND stage = ?',
      [runId, stage],
    );
    return row ? rowToWorkflowStageRun(row) : null;
  }

  private upsertTaskInternal(data: {
    stageRunId: string;
    workflowRunId: string;
    taskId: string;
    title: string;
    status?: WorkflowTaskStatus;
    taskOrder?: number;
    attempts?: number;
    duration?: number;
    artifacts?: string[];
    output?: unknown | null;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
    causedByTaskId?: string | null;
    startedAt?: string | null;
    completedAt?: string | null;
  }): WorkflowTask {
    const existing = this.db.get<WorkflowTaskRow>(
      'SELECT * FROM workflow_tasks WHERE stage_run_id = ? AND task_id = ?',
      [data.stageRunId, data.taskId],
    );

    const now = new Date().toISOString();
    const id = existing ? existing.id : `${data.stageRunId}/${data.taskId}`;
    const status = data.status ?? 'pending';
    const taskOrder = data.taskOrder ?? 0;
    const attempts = data.attempts ?? (existing ? existing.attempts : 0);
    const duration = data.duration ?? 0;
    const artifacts = data.artifacts ? JSON.stringify(data.artifacts) : (existing?.artifacts ?? '[]');

    if (existing) {
      const completedAt = status === 'completed' || status === 'failed'
        ? existing.completed_at ?? now
        : null;
      const startedAt = status === 'pending'
        ? null
        : data.startedAt ?? existing.started_at ?? now;
      this.db.run(
        `UPDATE workflow_tasks
         SET status = ?, attempts = ?, duration = ?, artifacts = ?, output = ?, reason = ?,
             caused_by_type = ?, caused_by_check_name = ?, caused_by_task_id = ?,
             started_at = ?, completed_at = ?, updated_at = ?
         WHERE id = ?`,
        [
          status,
          attempts,
          duration,
          artifacts,
          data.output !== undefined ? JSON.stringify(data.output) : existing.output,
          data.reason !== undefined ? data.reason : existing.reason,
          data.causedByType !== undefined ? data.causedByType : existing.caused_by_type,
          data.causedByCheckName !== undefined ? data.causedByCheckName : existing.caused_by_check_name,
          data.causedByTaskId !== undefined ? data.causedByTaskId : existing.caused_by_task_id,
          startedAt,
          completedAt,
          now,
          existing.id,
        ],
      );
      return this.findTaskById(existing.id)!;
    } else {
      this.db.run(
        `INSERT INTO workflow_tasks
         (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, output,
          reason, caused_by_type, caused_by_check_name, caused_by_task_id, started_at, completed_at, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          id, data.workflowRunId, data.stageRunId, data.taskId, data.title, status, taskOrder,
          attempts, duration, artifacts,
          data.output !== undefined ? JSON.stringify(data.output) : null,
          data.reason ?? null, data.causedByType ?? null, data.causedByCheckName ?? null, data.causedByTaskId ?? null,
          data.startedAt ?? now, data.completedAt ?? null, now, now,
        ],
      );
      return this.findTaskById(id)!;
    }
  }

  private upsertCheckInternal(data: {
    stageRunId: string;
    workflowRunId: string;
    checkName: string;
    title: string;
    status?: WorkflowCheckStatus;
    message?: string | null;
    output?: unknown | null;
    runCount?: number;
    lastRunAt?: string | null;
  }): WorkflowCheck {
    const existing = this.db.get<WorkflowCheckRow>(
      'SELECT * FROM workflow_checks WHERE stage_run_id = ? AND check_name = ?',
      [data.stageRunId, data.checkName],
    );

    const now = new Date().toISOString();
    const id = existing ? existing.id : `${data.stageRunId}/${data.checkName}`;
    const status = data.status ?? 'pending';
    const runCount = data.runCount ?? (existing ? existing.run_count : 0);

    if (existing) {
      this.db.run(
        `UPDATE workflow_checks
         SET status = ?, message = ?, output = ?, run_count = ?, last_run_at = ?, updated_at = ?
         WHERE id = ?`,
        [
          status,
          data.message !== undefined ? data.message : existing.message,
          data.output !== undefined ? JSON.stringify(data.output) : existing.output,
          runCount,
          data.lastRunAt ?? (status === 'pending' ? existing.last_run_at : now),
          now,
          existing.id,
        ],
      );
      return this.findCheckById(existing.id)!;
    } else {
      this.db.run(
        `INSERT INTO workflow_checks
         (id, workflow_run_id, stage_run_id, check_name, title, status, message, output, run_count, last_run_at, created_at, updated_at)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          id,
          data.workflowRunId,
          data.stageRunId,
          data.checkName,
          data.title,
          status,
          data.message ?? null,
          data.output !== undefined ? JSON.stringify(data.output) : null,
          runCount,
          data.lastRunAt ?? now,
          now,
          now,
        ],
      );
      return this.findCheckById(id)!;
    }
  }
}
