import { DatabaseManager } from '../db/database';
import { WorkflowRunRepo, WorkflowRunWithStageRuns, WorkflowStageRun, WorkflowRunStatus } from '../db/workflow-run-repo';
import { Stage } from '../types';

export class WorkflowRunService {
  private repo: WorkflowRunRepo;
  private db: DatabaseManager;

  constructor(db: DatabaseManager) {
    this.db = db;
    this.repo = new WorkflowRunRepo(db);
  }

  startRun(issueId: string, issueNumber: number, startedBy?: string | null): WorkflowRunWithStageRuns {
    return this.db.transaction(() => {
      const existing = this.repo.findActiveByIssueId(issueId);
      if (existing) {
        const full = this.repo.getActiveRunWithRelations(issueId);
        if (full) return full;
      }

      const run = this.repo.create({ issueId, issueNumber, startedBy });

      const stageOrderMap: Record<Stage, number> = {
        [Stage.Plan]: 0,
        [Stage.Build]: 1,
        [Stage.Check]: 2,
        [Stage.Integrate]: 3,
        [Stage.Done]: 4,
        [Stage.Backlog]: -1,
      };

      const stages: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate];
      const stageRuns: WorkflowStageRun[] = [];

      for (const stage of stages) {
        const sr = this.repo.createStageRun({
          workflowRunId: run.id,
          stage,
          stageOrder: stageOrderMap[stage],
        });
        stageRuns.push(sr);
      }

      const planStageRun = stageRuns.find(sr => sr.stage === Stage.Plan)!;
      this.seedPlanTasks(run.id, planStageRun.id);
      this.seedPlanChecks(run.id, planStageRun.id);

      const integrateStageRun = stageRuns.find(sr => sr.stage === Stage.Integrate)!;
      this.seedIntegrateTasks(run.id, integrateStageRun.id);
      this.seedIntegrateChecks(run.id, integrateStageRun.id);

      return this.repo.getActiveRunWithRelations(issueId)!;
    });
  }

  private seedPlanTasks(runId: string, planStageRunId: string): void {
    const planTasks = [
      { taskId: 'proposal', title: 'Generate proposal', order: 0 },
      { taskId: 'specs', title: 'Write specs', order: 1 },
      { taskId: 'design', title: 'Create design', order: 2 },
      { taskId: 'tasks', title: 'Generate tasks', order: 3 },
      { taskId: 'self-review', title: 'Self review', order: 4 },
    ];

    for (const task of planTasks) {
      this.repo.createTask({
        workflowRunId: runId,
        stageRunId: planStageRunId,
        taskId: task.taskId,
        title: task.title,
        taskOrder: task.order,
      });
    }
  }

  private seedPlanChecks(runId: string, planStageRunId: string): void {
    const planChecks = [
      { checkName: 'proposal-complete', title: 'Proposal complete' },
      { checkName: 'specs-complete', title: 'Specs complete' },
      { checkName: 'design-complete', title: 'Design complete' },
      { checkName: 'tasks-valid', title: 'Tasks valid' },
      { checkName: 'self-review-passed', title: 'Self review passed' },
      { checkName: 'user-approval', title: 'User approval' },
    ];

    for (const check of planChecks) {
      this.repo.createCheck({
        workflowRunId: runId,
        stageRunId: planStageRunId,
        checkName: check.checkName,
        title: check.title,
      });
    }
  }

  private seedIntegrateTasks(runId: string, integrateStageRunId: string): void {
    const integrateTasks = [
      { taskId: 'integrate:spec-sync', title: 'Sync specs', order: 0 },
      { taskId: 'integrate:archive-change', title: 'Archive change', order: 1 },
      { taskId: 'integrate:merge', title: 'Merge branch', order: 2 },
    ];

    for (const task of integrateTasks) {
      this.repo.createTask({
        workflowRunId: runId,
        stageRunId: integrateStageRunId,
        taskId: task.taskId,
        title: task.title,
        taskOrder: task.order,
      });
    }
  }

  private seedIntegrateChecks(runId: string, integrateStageRunId: string): void {
    this.repo.createCheck({
      workflowRunId: runId,
      stageRunId: integrateStageRunId,
      checkName: 'health:integrate',
      title: 'Post-merge health check',
    });
  }

  getActiveRunForIssue(issueId: string): WorkflowRunWithStageRuns | null {
    return this.repo.getActiveRunWithRelations(issueId);
  }

  setStageStarted(runId: string, stage: Stage): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.updateStageRunStatus(stageRun.id, 'running');
    this.repo.updateWorkflowRunStatus(runId, 'running', stage);
  }

  setStagePassed(runId: string, stage: Stage): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.updateStageRunStatus(stageRun.id, 'passed');
    this.repo.updateWorkflowRunStatus(runId, 'running', stage);
  }

  setStageFailed(runId: string, stage: Stage): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.updateStageRunStatus(stageRun.id, 'failed');
    this.repo.updateWorkflowRunStatus(runId, 'failed', stage);
  }

  setRunStatus(runId: string, status: WorkflowRunStatus, currentStage: Stage): void {
    this.repo.updateWorkflowRunStatus(runId, status, currentStage);
  }

  setStageAwaitingApproval(runId: string, stage: Stage): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.updateStageRunStatus(stageRun.id, 'awaiting-approval');
    this.repo.updateWorkflowRunStatus(runId, 'running', stage);
  }

  setApproval(runId: string, stage: Stage, approval: {
    status: string;
    output: unknown | null;
    requestedAt: string | null;
    respondedAt: string | null;
  }): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.setApproval(stageRun.id, approval);
  }

  upsertTask(runId: string, stage: Stage, task: {
    taskId: string;
    title: string;
    status?: import('../db/workflow-run-repo').WorkflowTaskStatus;
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
  }): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.upsertTask({
      stageRunId: stageRun.id,
      workflowRunId: runId,
      taskId: task.taskId,
      title: task.title,
      status: task.status,
      taskOrder: task.taskOrder,
      attempts: task.attempts,
      duration: task.duration,
      artifacts: task.artifacts,
      output: task.output,
      reason: task.reason,
      causedByType: task.causedByType,
      causedByCheckName: task.causedByCheckName,
      causedByTaskId: task.causedByTaskId,
      startedAt: task.startedAt,
      completedAt: task.completedAt,
    });
  }

  upsertCheck(runId: string, stage: Stage, check: {
    checkName: string;
    title: string;
    status?: import('../db/workflow-run-repo').WorkflowCheckStatus;
    message?: string | null;
    output?: unknown | null;
    runCount?: number;
    lastRunAt?: string | null;
  }): void {
    const stageRun = this.repo.findStageRunByStage(runId, stage);
    if (!stageRun) return;
    this.repo.upsertCheck({
      stageRunId: stageRun.id,
      workflowRunId: runId,
      checkName: check.checkName,
      title: check.title,
      status: check.status,
      message: check.message,
      output: check.output,
      runCount: check.runCount,
      lastRunAt: check.lastRunAt,
    });
  }

  materializeBuildTasks(runId: string, tasks: { id: string; title: string; order: number }[]): void {
    const stageRun = this.repo.findStageRunByStage(runId, Stage.Build);
    if (!stageRun) return;
    for (const task of tasks) {
      this.repo.upsertTask({
        workflowRunId: runId,
        stageRunId: stageRun.id,
        taskId: task.id,
        title: task.title,
        taskOrder: task.order,
      });
    }
  }
}