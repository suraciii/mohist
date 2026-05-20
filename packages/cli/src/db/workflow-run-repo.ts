import { DatabaseManager } from './database';
import { Stage } from '../types';
import {
  type CausedByMetadata,
  type StageRunSnapshot,
  type TaskResetMetadata,
  type WorkSourceState,
  type WorkItemAttempt,
  type WorkflowDefinitionSnapshot,
  type WorkflowRunSnapshot,
  WorkflowRun as DomainWorkflowRun,
} from '../workflow/model';
import {
  freezePointFromStageSnapshot,
  hydrateWorkflowRun,
  repairWorkflowRunSnapshot,
  workflowDefinitionSnapshotFromUnknown,
} from '../workflow/projection/workflow-run-snapshot';
import { DEFAULT_STAGE_DEFINITIONS, createDefaultWorkflowDefinitionSnapshot } from '../workflow/definition/default-workflow';

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
  workflowDefinition: unknown | null;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowStageRun {
  id: string;
  workflowRunId: string;
  stage: Stage;
  status: WorkflowStageRunStatus;
  stageOrder: number;
  attemptSequence: number;
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
  events: string[];
  output: unknown | null;
  reason: string | null;
  causedByType: string | null;
  causedByCheckName: string | null;
  causedByTaskId: string | null;
  resetByType: string | null;
  resetByTaskId: string | null;
  resetByEventName: string | null;
  resetReason: string | null;
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
  workflow_definition: string | null;
  created_at: string;
  updated_at: string;
}

interface WorkflowStageRunRow {
  id: string;
  workflow_run_id: string;
  stage: string;
  status: string;
  stage_order: number;
  attempt_sequence: number;
  approval_status: string | null;
  approval_output: string | null;
  approval_requested_at: string | null;
  approval_responded_at: string | null;
  started_at: string | null;
  completed_at: string | null;
  work_source_state: string | null;
  build_work_source_state: string | null;
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
  events: string;
  output: string | null;
  reason: string | null;
  caused_by_type: string | null;
  caused_by_check_name: string | null;
  caused_by_task_id: string | null;
  reset_by_type: string | null;
  reset_by_task_id: string | null;
  reset_by_event_name: string | null;
  reset_reason: string | null;
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

interface WorkflowWorkItemAttemptRow {
  id: string;
  workflow_run_id: string;
  stage: string;
  work_item_type: string;
  work_item_id: string;
  attempt_number: number;
  state: string;
  started_at: string;
  completed_at: string | null;
  output: string | null;
  error: string | null;
  diagnostic: string | null;
  queue_task_id: string | null;
  acp_session_id: string | null;
  coder_session_id: string | null;
  execution_id: string | null;
  process_pid: number | null;
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
    workflowDefinition: safeParseJson(row.workflow_definition),
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
    attemptSequence: row.attempt_sequence,
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
    events: JSON.parse(row.events || '[]'),
    output: row.output ? JSON.parse(row.output) : null,
    reason: row.reason,
    causedByType: row.caused_by_type,
    causedByCheckName: row.caused_by_check_name,
    causedByTaskId: row.caused_by_task_id,
    resetByType: row.reset_by_type,
    resetByTaskId: row.reset_by_task_id,
    resetByEventName: row.reset_by_event_name,
    resetReason: row.reset_reason,
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

function rowToWorkItemAttempt(row: WorkflowWorkItemAttemptRow): WorkItemAttempt {
  return {
    state: row.state as WorkItemAttempt['state'],
    attemptNumber: row.attempt_number,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    output: safeParseJson(row.output),
    error: row.error,
    diagnostic: row.diagnostic,
    queueTaskId: row.queue_task_id,
    acpSessionId: row.acp_session_id,
    coderSessionId: row.coder_session_id,
    executionId: row.execution_id,
    processPid: row.process_pid,
  };
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

function resetByFromTask(row: WorkflowTaskRow): TaskResetMetadata | null {
  if (!row.reset_by_type) return null;
  return {
    type: row.reset_by_type as TaskResetMetadata['type'],
    taskId: row.reset_by_task_id ?? undefined,
    eventName: row.reset_by_event_name ?? undefined,
    message: row.reset_reason ?? undefined,
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
    events: JSON.parse(row.events || '[]'),
    output: safeParseJson(row.output),
    reason: row.reason,
    causedBy: causedByFromTask(row),
    resetBy: resetByFromTask(row),
    latestAttempt: null,
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
    latestAttempt: null,
  };
}

function approvalFromStageRow(row: WorkflowStageRunRow): StageRunSnapshot['approval'] {
  if (!row.approval_status || !row.approval_requested_at) return null;
  const output = safeParseJson(row.approval_output);
  return {
    status: row.approval_status as 'awaiting' | 'approved' | 'rejected',
    output,
    requestedAt: row.approval_requested_at,
    respondedAt: row.approval_responded_at,
  };
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
    workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot;
  }): DomainWorkflowRun {
    return this.db.transaction(() => {
      const existing = this.loadRunningAggregate(data.issueId);
      if (existing) return existing;

      const id = `wr_${data.issueNumber}_${Date.now()}`;
      const { run } = DomainWorkflowRun.startWorkflow({
        id,
        issueId: data.issueId,
        issueNumber: data.issueNumber,
        workflowDefinitionSnapshot: data.workflowDefinitionSnapshot ?? createDefaultWorkflowDefinitionSnapshot(),
      });
      this.saveAggregate(run, data.startedBy ?? null);
      return this.loadRunningAggregate(data.issueId) ?? run;
    });
  }

  loadActiveAggregate(issueId: string): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status != 'cancelled' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row);
  }

  loadRunningAggregate(issueId: string): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status = 'running' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row);
  }

  loadLatestAggregate(issueId: string): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>(
      `SELECT * FROM workflow_runs WHERE issue_id = ? AND status != 'cancelled' ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    if (!row) return null;
    return this.loadAggregateByRow(row);
  }

  loadAggregateById(id: string): DomainWorkflowRun | null {
    const row = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [id]);
    if (!row) return null;
    return this.loadAggregateByRow(row);
  }

  saveAggregate(run: DomainWorkflowRun, startedBy?: string | null): void {
    const snapshot = run.snapshot();
    this.db.transaction(() => {
      this.saveAggregateSnapshot(snapshot, startedBy);
    });
  }

  private loadAggregateByRow(row: WorkflowRunRow): DomainWorkflowRun {
    return this.db.transaction(() => {
      const repaired = repairWorkflowRunSnapshot(this.snapshotFromRows(row));
      this.saveAggregateSnapshot(repaired, row.started_by);
      const freshRow = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [row.id]) ?? row;
      return hydrateWorkflowRun(this.snapshotFromRows(freshRow));
    });
  }

  private snapshotFromRows(row: WorkflowRunRow): WorkflowRunSnapshot {
    const workflowDefinitionSnapshot = workflowDefinitionSnapshotFromUnknown(safeParseJson(row.workflow_definition));
    const stageDefinitions = workflowDefinitionSnapshot?.compiledStageDefinitions ?? DEFAULT_STAGE_DEFINITIONS;
    const stageRows = this.db.all<WorkflowStageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? ORDER BY stage_order ASC',
      [row.id],
    );
    const latestAttempts = this.latestAttemptsByWorkItem(row.id);

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
        attemptSequence: stageRow.attempt_sequence,
        tasks: taskRows.map(taskRow => ({
          ...taskRowToSnapshot(taskRow),
          latestAttempt: latestAttempts.get(this.attemptKey(stageRow.stage as Stage, 'task', taskRow.task_id)) ?? null,
        })),
        checks: orderCheckSnapshots(stageRow.stage as Stage, checkRows.map(checkRow => ({
          ...checkRowToSnapshot(checkRow),
          latestAttempt: latestAttempts.get(this.attemptKey(stageRow.stage as Stage, 'check', checkRow.check_name)) ?? null,
        }))),
        approval: approvalFromStageRow(stageRow),
        failure: null,
        freezePoint: null,
        workSourceState: stageRow.work_source_state
          ? safeParseJson(stageRow.work_source_state) as WorkSourceState
          : undefined,
      };
      if (!stageSnapshot.workSourceState && stageRow.build_work_source_state) {
        stageSnapshot.workSourceState = safeParseJson(stageRow.build_work_source_state) as WorkSourceState;
      }
      const stageDefinition = stageDefinitions.find(definition => definition.stage === stageSnapshot.stage);
      stageSnapshot.freezePoint = freezePointFromStageSnapshot(stageSnapshot.stage, stageSnapshot, stageDefinition);
      return stageSnapshot;
    });

    const failureStage = stageRuns.find(stage => stage.status === 'failed');
    const snapshot: WorkflowRunSnapshot = {
      id: row.id,
      issueId: row.issue_id,
      issueNumber: row.issue_number,
      status: row.status as WorkflowRunStatus,
      currentStage: row.current_stage as Stage,
      stageOrder: stageDefinitions.map(definition => definition.stage),
      workflowDefinitionSnapshot: workflowDefinitionSnapshot ?? createDefaultWorkflowDefinitionSnapshot(),
      stageRuns,
      failure: failureStage?.failure ?? null,
    };
    return snapshot;
  }

  private latestAttemptsByWorkItem(workflowRunId: string): Map<string, WorkItemAttempt> {
    const rows = this.db.all<WorkflowWorkItemAttemptRow>(
      `SELECT *
       FROM workflow_work_item_attempts
       WHERE workflow_run_id = ?
       ORDER BY stage ASC, work_item_type ASC, work_item_id ASC, attempt_number DESC`,
      [workflowRunId],
    );
    const attempts = new Map<string, WorkItemAttempt>();
    for (const row of rows) {
      const key = this.attemptKey(row.stage as Stage, row.work_item_type as 'task' | 'check', row.work_item_id);
      if (!attempts.has(key)) attempts.set(key, rowToWorkItemAttempt(row));
    }
    return attempts;
  }

  private attemptKey(stage: Stage, workItemType: 'task' | 'check', workItemId: string): string {
    return `${stage}:${workItemType}:${workItemId}`;
  }

  private saveAggregateSnapshot(snapshot: WorkflowRunSnapshot, startedBy?: string | null): void {
    const now = new Date().toISOString();
    const existingRun = this.db.get<WorkflowRunRow>('SELECT * FROM workflow_runs WHERE id = ?', [snapshot.id]);
    if (existingRun) {
      this.db.run(
        `UPDATE workflow_runs
         SET issue_id = ?, issue_number = ?, status = ?, current_stage = ?, started_by = ?, workflow_definition = ?, updated_at = ?
         WHERE id = ?`,
        [
          snapshot.issueId,
          snapshot.issueNumber,
          snapshot.status,
          snapshot.currentStage,
          startedBy ?? existingRun.started_by,
          JSON.stringify(snapshot.workflowDefinitionSnapshot),
          now,
          snapshot.id,
        ],
      );
    } else {
      this.db.run(
        `INSERT INTO workflow_runs
         (id, issue_id, issue_number, status, current_stage, started_by, workflow_definition, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          snapshot.id,
          snapshot.issueId,
          snapshot.issueNumber,
          snapshot.status,
          snapshot.currentStage,
          startedBy ?? null,
          JSON.stringify(snapshot.workflowDefinitionSnapshot),
          now,
          now,
        ],
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
      const workSourceState = stageRun.workSourceState;
      const serializedWorkSourceState = workSourceState ? JSON.stringify(workSourceState) : null;
      const serializedBuildWorkSourceState = stageRun.stage === Stage.Build && workSourceState
        ? serializedWorkSourceState
        : null;

      if (existingStage) {
        this.db.run(
           `UPDATE workflow_stage_runs
           SET status = ?, stage_order = ?, attempt_sequence = ?, approval_status = ?, approval_output = ?, approval_requested_at = ?,
               approval_responded_at = ?, started_at = ?, completed_at = ?, work_source_state = ?, build_work_source_state = ?, updated_at = ?
            WHERE id = ?`,
          [
            stageRun.status,
            stageRun.order,
            stageRun.attemptSequence ?? 1,
            approval?.status ?? null,
            approval ? JSON.stringify(approval.output) : null,
            approval?.requestedAt ?? null,
            approval?.respondedAt ?? null,
            startedAt,
            completedAt,
            serializedWorkSourceState,
            serializedBuildWorkSourceState,
            now,
            stageRunId,
          ],
        );
      } else {
        this.db.run(
          `INSERT INTO workflow_stage_runs
           (id, workflow_run_id, stage, status, stage_order, attempt_sequence, approval_status, approval_output,
            approval_requested_at, approval_responded_at, started_at, completed_at, work_source_state, build_work_source_state, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          [
            stageRunId,
            snapshot.id,
            stageRun.stage,
            stageRun.status,
            stageRun.order,
            stageRun.attemptSequence ?? 1,
            approval?.status ?? null,
            approval ? JSON.stringify(approval.output) : null,
            approval?.requestedAt ?? null,
            approval?.respondedAt ?? null,
            startedAt,
            completedAt,
            serializedWorkSourceState,
            serializedBuildWorkSourceState,
            now,
            now,
          ],
        );
      }

      this.pruneStageRunChildren(stageRunId, stageRun.tasks.map(task => task.id), stageRun.checks.map(check => check.name));

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
          events: task.events,
          output: task.output,
          reason: task.reason,
          causedByType: task.causedBy?.type ?? null,
          causedByCheckName: task.causedBy?.checkName ?? null,
          causedByTaskId: task.causedBy?.taskId ?? null,
          resetByType: task.resetBy?.type ?? null,
          resetByTaskId: task.resetBy?.taskId ?? null,
          resetByEventName: task.resetBy?.eventName ?? null,
          resetReason: task.resetBy?.message ?? null,
        });
        this.syncWorkItemAttempt(snapshot.id, stageRun.stage, 'task', task.id, task.latestAttempt);
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
        this.syncWorkItemAttempt(snapshot.id, stageRun.stage, 'check', check.name, check.latestAttempt);
      }
    }
  }

  private syncWorkItemAttempt(
    workflowRunId: string,
    stage: Stage,
    workItemType: 'task' | 'check',
    workItemId: string,
    attempt: WorkItemAttempt | null,
  ): void {
    if (!attempt) {
      this.db.run(
        `DELETE FROM workflow_work_item_attempts
         WHERE workflow_run_id = ? AND stage = ? AND work_item_type = ? AND work_item_id = ?`,
        [workflowRunId, stage, workItemType, workItemId],
      );
      return;
    }

    const now = new Date().toISOString();
    const id = `${workflowRunId}/${stage}/${workItemType}/${workItemId}/${attempt.attemptNumber}`;
    this.db.run(
      `INSERT INTO workflow_work_item_attempts
       (id, workflow_run_id, stage, work_item_type, work_item_id, attempt_number, state, started_at,
        completed_at, output, error, diagnostic, queue_task_id, acp_session_id, coder_session_id,
        execution_id, process_pid, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(workflow_run_id, stage, work_item_type, work_item_id, attempt_number)
       DO UPDATE SET state = excluded.state,
         started_at = excluded.started_at,
         completed_at = excluded.completed_at,
         output = excluded.output,
         error = excluded.error,
         diagnostic = excluded.diagnostic,
         queue_task_id = excluded.queue_task_id,
         acp_session_id = excluded.acp_session_id,
         coder_session_id = excluded.coder_session_id,
         execution_id = excluded.execution_id,
         process_pid = excluded.process_pid,
         updated_at = excluded.updated_at`,
      [
        id,
        workflowRunId,
        stage,
        workItemType,
        workItemId,
        attempt.attemptNumber,
        attempt.state,
        attempt.startedAt,
        attempt.completedAt,
        attempt.output !== undefined ? JSON.stringify(attempt.output) : null,
        attempt.error,
        attempt.diagnostic,
        attempt.queueTaskId,
        attempt.acpSessionId,
        attempt.coderSessionId,
        attempt.executionId,
        attempt.processPid,
        now,
        now,
      ],
    );
  }

  private pruneStageRunChildren(stageRunId: string, taskIds: string[], checkNames: string[]): void {
    if (taskIds.length === 0) {
      this.db.run('DELETE FROM workflow_tasks WHERE stage_run_id = ?', [stageRunId]);
    } else {
      const placeholders = taskIds.map(() => '?').join(', ');
      this.db.run(
        `DELETE FROM workflow_tasks WHERE stage_run_id = ? AND task_id NOT IN (${placeholders})`,
        [stageRunId, ...taskIds],
      );
    }

    if (checkNames.length === 0) {
      this.db.run('DELETE FROM workflow_checks WHERE stage_run_id = ?', [stageRunId]);
    } else {
      const placeholders = checkNames.map(() => '?').join(', ');
      this.db.run(
        `DELETE FROM workflow_checks WHERE stage_run_id = ? AND check_name NOT IN (${placeholders})`,
        [stageRunId, ...checkNames],
      );
    }
  }

  create(data: {
    issueId: string;
    issueNumber: number;
    startedBy?: string | null;
  }): WorkflowRun {
    const now = new Date().toISOString();
    const id = `wr_${data.issueNumber}_${Date.now()}`;
    const workflowDefinitionSnapshot = createDefaultWorkflowDefinitionSnapshot(now);

    this.db.run(
      `INSERT INTO workflow_runs
       (id, issue_id, issue_number, status, current_stage, started_by, workflow_definition, created_at, updated_at)
       VALUES (?, ?, ?, 'running', 'plan', ?, ?, ?, ?)`,
      [id, data.issueId, data.issueNumber, data.startedBy ?? null, JSON.stringify(workflowDefinitionSnapshot), now, now],
    );

    return {
      id,
      issueId: data.issueId,
      issueNumber: data.issueNumber,
      status: 'running',
      currentStage: Stage.Plan,
      startedBy: data.startedBy ?? null,
      workflowDefinition: workflowDefinitionSnapshot,
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
      attemptSequence: 1,
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
    resetByType?: string | null;
    resetByTaskId?: string | null;
    resetByEventName?: string | null;
    resetReason?: string | null;
  }): WorkflowTask {
    const now = new Date().toISOString();
    const id = `${data.stageRunId}/${data.taskId}`;

    this.db.run(
      `INSERT INTO workflow_tasks
       (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, artifacts, created_at, updated_at,
        reason, caused_by_type, caused_by_check_name, caused_by_task_id, reset_by_type, reset_by_task_id,
        reset_by_event_name, reset_reason)
       VALUES (?, ?, ?, ?, ?, 'pending', ?, '[]', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        id, data.workflowRunId, data.stageRunId, data.taskId, data.title, data.taskOrder ?? 0, now, now,
        data.reason ?? null, data.causedByType ?? null, data.causedByCheckName ?? null, data.causedByTaskId ?? null,
        data.resetByType ?? null, data.resetByTaskId ?? null, data.resetByEventName ?? null, data.resetReason ?? null,
      ],
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
      events: [],
      output: null,
      reason: data.reason ?? null,
      causedByType: data.causedByType ?? null,
      causedByCheckName: data.causedByCheckName ?? null,
      causedByTaskId: data.causedByTaskId ?? null,
      resetByType: data.resetByType ?? null,
      resetByTaskId: data.resetByTaskId ?? null,
      resetByEventName: data.resetByEventName ?? null,
      resetReason: data.resetReason ?? null,
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
    events?: string[];
    output?: unknown | null;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
    causedByTaskId?: string | null;
    resetByType?: string | null;
    resetByTaskId?: string | null;
    resetByEventName?: string | null;
    resetReason?: string | null;
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
    const events = data.events ? JSON.stringify(data.events) : (existing?.events ?? '[]');

    if (existing) {
      const completedAt = status === 'completed' || status === 'failed'
        ? existing.completed_at ?? now
        : null;
      const startedAt = status === 'pending'
        ? null
        : data.startedAt ?? existing.started_at ?? now;
      this.db.run(
        `UPDATE workflow_tasks
         SET status = ?, attempts = ?, duration = ?, artifacts = ?, events = ?, output = ?, reason = ?,
             caused_by_type = ?, caused_by_check_name = ?, caused_by_task_id = ?,
             reset_by_type = ?, reset_by_task_id = ?, reset_by_event_name = ?, reset_reason = ?,
             started_at = ?, completed_at = ?, updated_at = ?
         WHERE id = ?`,
        [
          status,
          attempts,
          duration,
          artifacts,
          events,
          data.output !== undefined ? JSON.stringify(data.output) : existing.output,
          data.reason !== undefined ? data.reason : existing.reason,
          data.causedByType !== undefined ? data.causedByType : existing.caused_by_type,
          data.causedByCheckName !== undefined ? data.causedByCheckName : existing.caused_by_check_name,
          data.causedByTaskId !== undefined ? data.causedByTaskId : existing.caused_by_task_id,
          data.resetByType !== undefined ? data.resetByType : existing.reset_by_type,
          data.resetByTaskId !== undefined ? data.resetByTaskId : existing.reset_by_task_id,
          data.resetByEventName !== undefined ? data.resetByEventName : existing.reset_by_event_name,
          data.resetReason !== undefined ? data.resetReason : existing.reset_reason,
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
         (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, events, output,
          reason, caused_by_type, caused_by_check_name, caused_by_task_id, reset_by_type, reset_by_task_id,
          reset_by_event_name, reset_reason, started_at, completed_at, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          id, data.workflowRunId, data.stageRunId, data.taskId, data.title, status, taskOrder,
          attempts, duration, artifacts,
          events,
          data.output !== undefined ? JSON.stringify(data.output) : null,
          data.reason ?? null, data.causedByType ?? null, data.causedByCheckName ?? null, data.causedByTaskId ?? null,
          data.resetByType ?? null, data.resetByTaskId ?? null, data.resetByEventName ?? null, data.resetReason ?? null,
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
