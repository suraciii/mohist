import { 
  DatabaseManager,
  initializeDatabase,
  ProjectRepo, 
  IssueRepo, 
  ConfigRepo,
  CommentRepo,
  LabelRepo,
  WorkflowLogRepo,
  QuestionRepo,
  ExploreSessionRepo,
  ExploreMessageRepo,
  AgentSessionMessageRepo,
  CoderSessionRepo
} from '../db';
import { initializeDefaultConfig } from '../db/config-repo';

export class StateManager {
  private projectRepo: ProjectRepo;
  private issueRepo: IssueRepo;
  private configRepo: ConfigRepo;
  private commentRepo: CommentRepo;
  private labelRepo: LabelRepo;
  private workflowLogRepo: WorkflowLogRepo;
  private questionRepo: QuestionRepo;
  private exploreSessionRepo: ExploreSessionRepo;
  private exploreMessageRepo: ExploreMessageRepo;
  private agentSessionMessageRepo: AgentSessionMessageRepo;
  private coderSessionRepo: CoderSessionRepo;
  private initialized: boolean = false;

  constructor(db: DatabaseManager) {
    initializeDatabase(db);
    
    this.projectRepo = new ProjectRepo(db);
    this.issueRepo = new IssueRepo(db);
    this.configRepo = new ConfigRepo(db);
    this.commentRepo = new CommentRepo(db);
    this.labelRepo = new LabelRepo(db);
    this.workflowLogRepo = new WorkflowLogRepo(db);
    this.questionRepo = new QuestionRepo(db);
    this.exploreSessionRepo = new ExploreSessionRepo(db);
    this.exploreMessageRepo = new ExploreMessageRepo(db);
    this.agentSessionMessageRepo = new AgentSessionMessageRepo(db);
    this.coderSessionRepo = new CoderSessionRepo(db);
    
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

  getQuestionRepo(): QuestionRepo {
    return this.questionRepo;
  }

  getExploreSessionRepo(): ExploreSessionRepo {
    return this.exploreSessionRepo;
  }

  getExploreMessageRepo(): ExploreMessageRepo {
    return this.exploreMessageRepo;
  }

  getAgentSessionMessageRepo(): AgentSessionMessageRepo {
    return this.agentSessionMessageRepo;
  }

  getCoderSessionRepo(): CoderSessionRepo {
    return this.coderSessionRepo;
  }
}
