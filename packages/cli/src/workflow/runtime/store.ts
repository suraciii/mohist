import type { WorkflowStore } from '@mohist/workflow';
import { WorkflowRun, type StageDefinition } from '@mohist/workflow';
import type { DatabaseManager } from '../../db/database';

interface WorkflowRunRow {
  id: string;
  definition: string;
  status: string;
  current_stage: string;
  created_at: string;
  updated_at: string;
}

interface StageRunRow {
  workflow_run_id: string;
  stage: string;
  order: number;
  started: boolean;
  initialized: boolean;
  approval_status: string | null;
  approval_output: string | null;
  approval_requested_at: string | null;
  approval_responded_at: string | null;
  failure_reason: string | null;
  failure_message: string | null;
}

interface TaskRow {
  workflow_run_id: string;
  stage: string;
  task_id: string;
  title: string;
  uses: string | null;
  with_json: string | null;
  status: string;
  order: number;
}

interface CheckRow {
  workflow_run_id: string;
  stage: string;
  check_name: string;
  title: string;
  uses: string | null;
  with_json: string | null;
  status: string;
  message: string | null;
  output_json: string | null;
  order: number;
}

export class WorkflowStoreAdapter implements WorkflowStore {
  constructor(
    private db: DatabaseManager,
  ) {}

  async load(id: string): Promise<WorkflowRun | null> {
    const runRow = this.db.get<WorkflowRunRow>(
      'SELECT * FROM workflow_runs WHERE id = ?',
      [id]
    );

    if (!runRow) return null;

    const definitionStages = JSON.parse(runRow.definition) as StageDefinition[];
    const run = new WorkflowRun(runRow.id, definitionStages);

    if (runRow.status === 'running' || runRow.status === 'paused' || runRow.status === 'passed' || runRow.status === 'failed') {
      run.start();
    }

    if (runRow.status === 'paused') {
      run.pause();
    }

    const stageRows = this.db.all<StageRunRow>(
      'SELECT * FROM workflow_stage_runs WHERE workflow_run_id = ? ORDER BY order ASC',
      [id]
    );

    const taskRows = this.db.all<TaskRow>(
      'SELECT * FROM workflow_tasks WHERE workflow_run_id = ? ORDER BY stage, order ASC',
      [id]
    );

    const checkRows = this.db.all<CheckRow>(
      'SELECT * FROM workflow_checks WHERE workflow_run_id = ? ORDER BY stage, order ASC',
      [id]
    );

    for (const stageRow of stageRows) {
      const stageRun = run.stageRuns.find(s => s.stage === stageRow.stage);
      if (!stageRun) continue;

      if (stageRow.started) {
        stageRun.start();
      }

      const stageTasks = taskRows.filter(t => t.stage === stageRow.stage);
      if (stageTasks.length > 0) {
        stageRun.initTasks(stageTasks.map(t => ({
          id: t.task_id,
          title: t.title,
          uses: t.uses ?? undefined,
          with: t.with_json ? JSON.parse(t.with_json) : undefined,
        })));

        for (const task of stageTasks) {
          const taskRun = stageRun.tasks.find(t => t.id === task.task_id);
          if (!taskRun) continue;

          switch (task.status) {
            case 'running':
              taskRun.start();
              break;
            case 'completed':
              taskRun.start();
              taskRun.complete();
              break;
            case 'failed':
              taskRun.start();
              taskRun.fail();
              break;
          }
        }
      } else if (stageRow.initialized) {
        stageRun.initTasks([]);
      }

      const stageChecks = checkRows.filter(c => c.stage === stageRow.stage);
      for (const check of stageChecks) {
        const checkRun = stageRun.checks.find(c => c.name === check.check_name);
        if (!checkRun) continue;

        checkRun.message = check.message;
        checkRun.output = check.output_json ? JSON.parse(check.output_json) : null;

        switch (check.status) {
          case 'passed':
            checkRun.pass();
            break;
          case 'failed':
            checkRun.fail();
            break;
        }
      }

      if (stageRow.approval_status) {
        stageRun.approval = {
          status: stageRow.approval_status as 'awaiting' | 'approved' | 'rejected',
          output: stageRow.approval_output ? JSON.parse(stageRow.approval_output) : null,
          requestedAt: stageRow.approval_requested_at || new Date().toISOString(),
          respondedAt: stageRow.approval_responded_at,
        };
      }

      if (stageRow.failure_reason) {
        stageRun.failure = {
          reason: stageRow.failure_reason as 'task-failed' | 'check-unrepaired' | 'approval-rejected',
          stage: stageRow.stage,
          message: stageRow.failure_message ?? undefined,
        };
      }
    }

    const currentStage = run.stageRuns.find(s => s.stage === runRow.current_stage);
    if (currentStage) {
      run.currentStage = currentStage;
    }

    return run;
  }

  async save(run: WorkflowRun): Promise<void> {
    const now = new Date().toISOString();
    const runStatus = run.status;

    const existingRun = this.db.get<WorkflowRunRow>(
      'SELECT id FROM workflow_runs WHERE id = ?',
      [run.id]
    );

    if (existingRun) {
      this.db.run(`
        UPDATE workflow_runs 
        SET status = ?, current_stage = ?, updated_at = ?
        WHERE id = ?
      `, [runStatus, run.currentStage.stage, now, run.id]);
    } else {
      this.db.run(`
        INSERT INTO workflow_runs (id, definition, status, current_stage, created_at, updated_at)
        VALUES (?, ?, ?, ?, ?, ?)
      `, [run.id, JSON.stringify(run.definitionStages), runStatus, run.currentStage.stage, now, now]);
    }

    this.db.run('DELETE FROM workflow_stage_runs WHERE workflow_run_id = ?', [run.id]);
    this.db.run('DELETE FROM workflow_tasks WHERE workflow_run_id = ?', [run.id]);
    this.db.run('DELETE FROM workflow_checks WHERE workflow_run_id = ?', [run.id]);

    for (const stageRun of run.stageRuns) {
      const approvalOutput = stageRun.approval?.output !== undefined && stageRun.approval?.output !== null 
        ? JSON.stringify(stageRun.approval.output) 
        : null;

      this.db.run(`
        INSERT INTO workflow_stage_runs (workflow_run_id, stage, order, started, initialized, approval_status, approval_output, approval_requested_at, approval_responded_at, failure_reason, failure_message)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      `, [
        run.id,
        stageRun.stage,
        stageRun.order,
        stageRun.status !== 'pending',
        stageRun.initialized,
        stageRun.approval?.status ?? null,
        approvalOutput,
        stageRun.approval?.requestedAt ?? null,
        stageRun.approval?.respondedAt ?? null,
        stageRun.failure?.reason ?? null,
        stageRun.failure?.message ?? null,
      ]);

      for (let i = 0; i < stageRun.tasks.length; i++) {
        const task = stageRun.tasks[i];
        this.db.run(`
          INSERT INTO workflow_tasks (workflow_run_id, stage, task_id, title, uses, with_json, status, order)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        `, [
          run.id,
          stageRun.stage,
          task.id,
          task.title,
          task.uses ?? null,
          task.withInput ? JSON.stringify(task.withInput) : null,
          task.status,
          i,
        ]);
      }

      for (let i = 0; i < stageRun.checks.length; i++) {
        const check = stageRun.checks[i];
        this.db.run(`
          INSERT INTO workflow_checks (workflow_run_id, stage, check_name, title, uses, with_json, status, message, output_json, order)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        `, [
          run.id,
          stageRun.stage,
          check.name,
          check.title,
          check.uses ?? null,
          check.withInput ? JSON.stringify(check.withInput) : null,
          check.status,
          check.message,
          check.output ? JSON.stringify(check.output) : null,
          i,
        ]);
      }
    }
  }
}

export function migrateWorkflowRunsTable(db: DatabaseManager): void {
  db.run(`
    CREATE TABLE IF NOT EXISTS workflow_runs (
      id TEXT PRIMARY KEY,
      definition TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'pending',
      current_stage TEXT NOT NULL,
      created_at TEXT NOT NULL,
      updated_at TEXT NOT NULL
    )
  `);

  db.run(`
    CREATE TABLE IF NOT EXISTS workflow_stage_runs (
      workflow_run_id TEXT NOT NULL,
      stage TEXT NOT NULL,
      order INTEGER NOT NULL,
      started BOOLEAN NOT NULL DEFAULT 0,
      initialized BOOLEAN NOT NULL DEFAULT 0,
      approval_status TEXT,
      approval_output TEXT,
      approval_requested_at TEXT,
      approval_responded_at TEXT,
      failure_reason TEXT,
      failure_message TEXT,
      PRIMARY KEY (workflow_run_id, stage),
      FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id)
    )
  `);

  db.run(`
    CREATE TABLE IF NOT EXISTS workflow_tasks (
      workflow_run_id TEXT NOT NULL,
      stage TEXT NOT NULL,
      task_id TEXT NOT NULL,
      title TEXT NOT NULL,
      uses TEXT,
      with_json TEXT,
      status TEXT NOT NULL,
      order INTEGER NOT NULL,
      PRIMARY KEY (workflow_run_id, stage, task_id),
      FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id)
    )
  `);

  db.run(`
    CREATE TABLE IF NOT EXISTS workflow_checks (
      workflow_run_id TEXT NOT NULL,
      stage TEXT NOT NULL,
      check_name TEXT NOT NULL,
      title TEXT NOT NULL,
      uses TEXT,
      with_json TEXT,
      status TEXT NOT NULL,
      message TEXT,
      output_json TEXT,
      order INTEGER NOT NULL,
      PRIMARY KEY (workflow_run_id, stage, check_name),
      FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id)
    )
  `);

  db.run(`
    CREATE INDEX IF NOT EXISTS idx_workflow_runs_status ON workflow_runs(status)
  `);
}
