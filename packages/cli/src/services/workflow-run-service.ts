import { DatabaseManager } from '../db/database';
import { WorkflowRunRepo, WorkflowRunWithStageRuns } from '../db/workflow-run-repo';
import { Stage } from '../types';
import type { WorkflowDefinitionSnapshot } from '@mohist/workflow/internal/model';
import { WorkflowApplicationService } from './workflow-application-service';

export class WorkflowRunService {
  private repo: WorkflowRunRepo;
  private db: DatabaseManager;

  constructor(db: DatabaseManager) {
    this.db = db;
    this.repo = new WorkflowRunRepo(db);
  }

  getDatabaseManager(): DatabaseManager {
    return this.db;
  }

  startRun(issueId: string, issueNumber: number, startedBy?: string | null, workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot): WorkflowRunWithStageRuns {
    new WorkflowApplicationService(this.db).startWorkflow({ issueId, issueNumber, startedBy, workflowDefinitionSnapshot });
    const run = this.repo.getActiveRunWithRelations(issueId);
    if (!run) throw new Error(`Failed to start WorkflowRun for issue ${issueId}`);
    return run;
  }

  getActiveRunForIssue(issueId: string): WorkflowRunWithStageRuns | null {
    return this.repo.getActiveRunWithRelations(issueId);
  }

  getLatestRunForIssue(issueId: string): WorkflowRunWithStageRuns | null {
    return this.repo.getLatestRunWithRelations(issueId);
  }

  canRetryStage(issueId: string, stage: Stage): boolean {
    const run = this.repo.loadLatestAggregate(issueId);
    if (!run) return false;
    return run.canRetryStage(stage);
  }

  materializeBuildTasks(runId: string, tasks: { id: string; title: string; order: number }[]): void {
    const run = this.repo.findById(runId);
    if (!run) return;
    if (run.currentStage !== Stage.Build) return;
    new WorkflowApplicationService(this.db).materializeTasks({
      issueId: run.issueId,
      stage: Stage.Build,
      tasks: tasks.map(task => ({ id: task.id, title: task.title, order: task.order })),
    });
  }
}
