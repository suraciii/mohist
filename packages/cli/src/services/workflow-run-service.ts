import { DatabaseManager } from '../db/database';

export class WorkflowRunService {
  private db: DatabaseManager;

  constructor(db: DatabaseManager) {
    this.db = db;
  }

  getDatabaseManager(): DatabaseManager {
    return this.db;
  }

  // Legacy compatibility wrapper for callers that still expect a service object.
  // Workflow execution and persistence are owned by the .NET server.
}
