import { 
  getDatabase, 
  initializeDatabase,
  ProjectRepo, 
  IssueRepo, 
  ConfigRepo,
  CommentRepo,
  LabelRepo,
  WorkflowLogRepo
} from '../db';
import { initializeDefaultConfig } from '../db/config-repo';

export class StateManager {
  private projectRepo: ProjectRepo;
  private issueRepo: IssueRepo;
  private configRepo: ConfigRepo;
  private commentRepo: CommentRepo;
  private labelRepo: LabelRepo;
  private workflowLogRepo: WorkflowLogRepo;
  private initialized: boolean = false;

  constructor() {
    const db = getDatabase();
    initializeDatabase(db);
    
    this.projectRepo = new ProjectRepo(db);
    this.issueRepo = new IssueRepo(db);
    this.configRepo = new ConfigRepo(db);
    this.commentRepo = new CommentRepo(db);
    this.labelRepo = new LabelRepo(db);
    this.workflowLogRepo = new WorkflowLogRepo(db);
    
    initializeDefaultConfig(this.configRepo);
    this.initialized = true;
  }

  isInitialized(): boolean {
    return this.initialized;
  }

  getProjectRepo(): ProjectRepo {
    return this.projectRepo;
  }

  getIssueRepo(): IssueRepo {
    return this.issueRepo;
  }

  getConfigRepo(): ConfigRepo {
    return this.configRepo;
  }

  getCommentRepo(): CommentRepo {
    return this.commentRepo;
  }

  getLabelRepo(): LabelRepo {
    return this.labelRepo;
  }

  getWorkflowLogRepo(): WorkflowLogRepo {
    return this.workflowLogRepo;
  }
}

let stateManagerInstance: StateManager | null = null;

export function getStateManager(): StateManager {
  if (!stateManagerInstance) {
    stateManagerInstance = new StateManager();
  }
  return stateManagerInstance;
}

export function resetStateManager(): StateManager {
  stateManagerInstance = new StateManager();
  return stateManagerInstance;
}
