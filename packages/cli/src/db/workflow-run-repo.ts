import { DatabaseManager } from './database';
import { Stage } from '../types';

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

export class WorkflowRunRepo {
  constructor(private db: DatabaseManager) {}

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
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status = 'running' ORDER BY created_at DESC LIMIT 1`,
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

  updateStageRunStatus(stageRunId: string, status: WorkflowStageRunStatus): void {
    const now = new Date().toISOString();
    if (status === 'running') {
      this.db.run(
        'UPDATE workflow_stage_runs SET status = ?, started_at = ?, updated_at = ? WHERE id = ?',
        [status, now, now, stageRunId],
      );
    } else if (status === 'passed' || status === 'failed' || status === 'awaiting-approval') {
      this.db.run(
        'UPDATE workflow_stage_runs SET status = ?, completed_at = ?, updated_at = ? WHERE id = ?',
        [status, now, now, stageRunId],
      );
    } else {
      this.db.run(
        'UPDATE workflow_stage_runs SET status = ?, updated_at = ? WHERE id = ?',
        [status, now, stageRunId],
      );
    }
  }

  updateWorkflowRunStatus(runId: string, status: WorkflowRunStatus, currentStage: Stage): void {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE workflow_runs SET status = ?, current_stage = ?, updated_at = ? WHERE id = ?',
      [status, currentStage, now, runId],
    );
  }

  setApproval(stageRunId: string, approval: {
    status: string;
    output: unknown | null;
    requestedAt: string | null;
    respondedAt: string | null;
  }): void {
    const now = new Date().toISOString();
    this.db.run(
      `UPDATE workflow_stage_runs
       SET approval_status = ?, approval_output = ?, approval_requested_at = ?, approval_responded_at = ?, updated_at = ?
       WHERE id = ?`,
      [
        approval.status,
        approval.output !== null ? JSON.stringify(approval.output) : null,
        approval.requestedAt,
        approval.respondedAt,
        now,
        stageRunId,
      ],
    );
  }

  upsertTask(data: {
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
      const completedAt = (status === 'completed' || status === 'failed') && !existing.completed_at ? now : (existing.completed_at ?? null);
      this.db.run(
        `UPDATE workflow_tasks
         SET status = ?, attempts = ?, duration = ?, artifacts = ?, output = ?, reason = ?,
             caused_by_type = ?, caused_by_check_name = ?, caused_by_task_id = ?,
             started_at = COALESCE(started_at, ?), completed_at = ?, updated_at = ?
         WHERE id = ?`,
        [
          status,
          attempts,
          duration,
          artifacts,
          data.output !== undefined ? JSON.stringify(data.output) : existing.output,
          data.reason ?? existing.reason,
          data.causedByType ?? existing.caused_by_type,
          data.causedByCheckName ?? existing.caused_by_check_name,
          data.causedByTaskId ?? existing.caused_by_task_id,
          data.startedAt ?? existing.started_at,
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

  upsertCheck(data: {
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
          data.message ?? existing.message,
          data.output !== undefined ? JSON.stringify(data.output) : existing.output,
          runCount,
          data.lastRunAt ?? now,
          now,
          existing.id,
        ],
      );
      return this.findCheckById(existing.id)!;
    } else {
      this.db.run(
        `INSERT INTO workflow_checks
         (id, workflow_run_id, stage_run_id, check_name, title, status, run_count, last_run_at, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [id, data.workflowRunId, data.stageRunId, data.checkName, data.title, status, runCount, data.lastRunAt ?? now, now, now],
      );
      return this.findCheckById(id)!;
    }
  }
}