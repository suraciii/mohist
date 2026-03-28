import { Project, Issue, Stage, IssueStatus, Comment } from '../types';
import { 
  getDatabase, 
  initializeDatabase,
  ProjectRepo, 
  IssueRepo, 
  ConfigRepo,
  CommentRepo,
  LabelRepo 
} from '../db';
import { initializeDefaultConfig } from '../db/config-repo';

export class StateManager {
  private projectRepo: ProjectRepo;
  private issueRepo: IssueRepo;
  private configRepo: ConfigRepo;
  private commentRepo: CommentRepo;
  private labelRepo: LabelRepo;
  private initialized: boolean = false;

  constructor() {
    const db = getDatabase();
    initializeDatabase(db);
    
    this.projectRepo = new ProjectRepo(db);
    this.issueRepo = new IssueRepo(db);
    this.configRepo = new ConfigRepo(db);
    this.commentRepo = new CommentRepo(db);
    this.labelRepo = new LabelRepo(db);
    
    initializeDefaultConfig(this.configRepo);
    this.initialized = true;
  }

  isInitialized(): boolean {
    return this.initialized;
  }

  loadProjects(): Project[] {
    return this.projectRepo.findAll();
  }

  getProjectById(id: string): Project | null {
    return this.projectRepo.findById(id);
  }

  getProjectByName(name: string): Project | null {
    return this.projectRepo.findByName(name);
  }

  getProjectByPath(path: string): Project | null {
    return this.projectRepo.findByPath(path);
  }

  saveProject(project: Omit<Project, 'id' | 'createdAt' | 'updatedAt'>): Project {
    return this.projectRepo.create(project);
  }

  deleteProject(id: string): boolean {
    this.issueRepo.deleteByProjectCascade(id);
    return this.projectRepo.delete(id);
  }

  loadIssues(projectId: string): Issue[] {
    return this.issueRepo.findAll({ projectId });
  }

  getIssueByNumber(projectId: string, number: number): Issue | null {
    return this.issueRepo.findByNumber(projectId, number);
  }

  getIssueById(id: string): Issue | null {
    return this.issueRepo.findById(id);
  }

  createIssue(projectId: string, title: string, body?: string, labels?: string[]): Issue {
    const number = this.issueRepo.getNextNumber(projectId);
    return this.issueRepo.create({
      number,
      projectId,
      title,
      body,
      labels,
    });
  }

  updateIssueStage(issueId: string, stage: Stage): Issue | null {
    return this.issueRepo.updateStage(issueId, stage);
  }

  updateIssueStatus(issueId: string, status: IssueStatus): Issue | null {
    return this.issueRepo.updateStatus(issueId, status);
  }

  getCurrentProjectId(): string | null {
    return this.configRepo.get('currentProjectId');
  }

  setCurrentProjectId(id: string): void {
    this.configRepo.set('currentProjectId', id);
  }

  clearCurrentProject(): void {
    this.configRepo.delete('currentProjectId');
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

  createComment(issueId: string, body: string): Comment {
    return this.commentRepo.create({ issueId, body });
  }

  getCommentsByIssue(issueId: string): Comment[] {
    return this.commentRepo.findByIssue(issueId);
  }

  getLabels(projectId: string): string[] {
    return this.labelRepo.findAllUsed(projectId);
  }

  updateIssueLabels(issueId: string, labels: string[]): Issue | null {
    return this.issueRepo.update(issueId, { labels });
  }

  addIssueLabel(issueId: string, label: string): Issue | null {
    return this.issueRepo.addLabel(issueId, label);
  }

  removeIssueLabel(issueId: string, label: string): Issue | null {
    return this.issueRepo.removeLabel(issueId, label);
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
