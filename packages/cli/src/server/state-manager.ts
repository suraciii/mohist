import { 
  DatabaseManager,
  initializeDatabase,
  ProjectRepo, 
  IssueRepo, 
  ConfigRepo,
  CommentRepo,
  LabelRepo,
  WorkflowLogRepo,
  SessionStreamLogRepo,
  QuestionRepo,
  ExploreSessionRepo,
  ExploreMessageRepo,
  CoderSessionRepo,
  PipelineCheckpointRepo,
  ScheduleRepo,
  IssueTaskQueueRepo,
  CheckSuiteRepo,
  StageExecutionRepo,
  IssueStartPrerequisiteRepo,
  EpicRepo
} from '../db';
import { initializeDefaultConfig } from '../db/config-repo';

export class StateManager {
  private projectRepo: ProjectRepo;
  private issueRepo: IssueRepo;
  private configRepo: ConfigRepo;
  private commentRepo: CommentRepo;
  private labelRepo: LabelRepo;
  private workflowLogRepo: WorkflowLogRepo;
  private sessionStreamLogRepo: SessionStreamLogRepo;
  private questionRepo: QuestionRepo;
  private exploreSessionRepo: ExploreSessionRepo;
  private exploreMessageRepo: ExploreMessageRepo;
  private coderSessionRepo: CoderSessionRepo;
  private pipelineCheckpointRepo: PipelineCheckpointRepo;
  private scheduleRepo: ScheduleRepo;
  private issueTaskQueueRepo: IssueTaskQueueRepo;
  private checkSuiteRepo: CheckSuiteRepo;
  private stageExecutionRepo: StageExecutionRepo;
  private issueStartPrerequisiteRepo: IssueStartPrerequisiteRepo;
  private epicRepo: EpicRepo;
  private initialized: boolean = false;

  constructor(db: DatabaseManager) {
    initializeDatabase(db);
    
    this.projectRepo = new ProjectRepo(db);
    this.issueRepo = new IssueRepo(db);
    this.configRepo = new ConfigRepo(db);
    this.commentRepo = new CommentRepo(db);
    this.labelRepo = new LabelRepo(db);
    this.workflowLogRepo = new WorkflowLogRepo(db);
    this.sessionStreamLogRepo = new SessionStreamLogRepo(db);
    this.questionRepo = new QuestionRepo(db);
    this.exploreSessionRepo = new ExploreSessionRepo(db);
    this.exploreMessageRepo = new ExploreMessageRepo(db);
    this.coderSessionRepo = new CoderSessionRepo(db);
    this.pipelineCheckpointRepo = new PipelineCheckpointRepo(db);
    this.scheduleRepo = new ScheduleRepo(db);
    this.issueTaskQueueRepo = new IssueTaskQueueRepo(db);
    this.checkSuiteRepo = new CheckSuiteRepo(db);
    this.stageExecutionRepo = new StageExecutionRepo(db);
    this.issueStartPrerequisiteRepo = new IssueStartPrerequisiteRepo(db);
    this.epicRepo = new EpicRepo(db);
    
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

  getSessionStreamLogRepo(): SessionStreamLogRepo {
    return this.sessionStreamLogRepo;
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

  getCoderSessionRepo(): CoderSessionRepo {
    return this.coderSessionRepo;
  }

  getPipelineCheckpointRepo(): PipelineCheckpointRepo {
    return this.pipelineCheckpointRepo;
  }

  getScheduleRepo(): ScheduleRepo {
    return this.scheduleRepo;
  }

  getIssueTaskQueueRepo(): IssueTaskQueueRepo {
    return this.issueTaskQueueRepo;
  }

  getCheckSuiteRepo(): CheckSuiteRepo {
    return this.checkSuiteRepo;
  }

  getStageExecutionRepo(): StageExecutionRepo {
    return this.stageExecutionRepo;
  }

  getIssueStartPrerequisiteRepo(): IssueStartPrerequisiteRepo {
    return this.issueStartPrerequisiteRepo;
  }

  getEpicRepo(): EpicRepo {
    return this.epicRepo;
  }
}
