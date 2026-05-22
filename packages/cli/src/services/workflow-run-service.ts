import { DatabaseManager } from '../db/database';

// TODO: Replace old workflow repo usage with new WorkflowStoreAdapter once fully migrated
export class WorkflowRunService {
  private db: DatabaseManager;

  constructor(db: DatabaseManager) {
    this.db = db;
  }

  getDatabaseManager(): DatabaseManager {
    return this.db;
  }

  // TODO: These methods use old workflow schema - will be replaced by new WorkflowStoreAdapter
  // getActiveRunForIssue, getLatestRunForIssue, canRetryStage, materializeBuildTasks
}
