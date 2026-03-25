import { Project, Issue, Task, Stage, IssueStatus } from '../types';
import { 
  getDatabase, 
  initializeDatabase,
  ProjectRepo, 
  IssueRepo, 
  TaskRepo,
  ConfigRepo 
} from '../db';
import { initializeDefaultConfig } from '../db/config-repo';

export class StateManager {
  private projectRepo: ProjectRepo;
  private issueRepo: IssueRepo;
  private taskRepo: TaskRepo;
  private configRepo: ConfigRepo;
  private initialized: boolean = false;

  constructor() {
    const db = getDatabase();
    initializeDatabase(db);
    
    this.projectRepo = new ProjectRepo(db);
    this.issueRepo = new IssueRepo(db);
    this.taskRepo = new TaskRepo(db);
    this.configRepo = new ConfigRepo(db);
    
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
    this.taskRepo.deleteByProject(id);
    this.issueRepo.deleteByProject(id);
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

  createIssue(projectId: string, title: string, body?: string): Issue {
    const number = this.issueRepo.getNextNumber(projectId);
    return this.issueRepo.create({
      number,
      projectId,
      title,
      body,
    });
  }

  updateIssueStage(issueId: string, stage: Stage): Issue | null {
    return this.issueRepo.updateStage(issueId, stage);
  }

  updateIssueStatus(issueId: string, status: IssueStatus): Issue | null {
    return this.issueRepo.updateStatus(issueId, status);
  }

  loadTasks(projectId: string): Task[] {
    return this.taskRepo.findAll({ projectId });
  }

  getRunningTasks(): Task[] {
    return this.taskRepo.findRunning();
  }

  getPendingTasks(): Task[] {
    return this.taskRepo.findPending();
  }

  createTask(issueId: string, projectId: string, stage: Stage): Task {
    return this.taskRepo.create({
      issueId,
      projectId,
      stage,
    });
  }

  updateTaskStatus(taskId: string, status: Task['status'], error?: string): Task | null {
    return this.taskRepo.updateStatus(taskId, status, error);
  }

  setTaskAgentPid(taskId: string, pid: number): void {
    this.taskRepo.setAgentPid(taskId, pid);
  }

  clearTaskAgentPid(taskId: string): void {
    this.taskRepo.clearAgentPid(taskId);
  }

  recoverState(): { projects: Project[]; activeTasks: Task[] } {
    const projects = this.loadProjects();
    const runningTasks = this.getRunningTasks();
    
    for (const task of runningTasks) {
      this.taskRepo.updateStatus(task.id!, 'failed', 'Server restarted');
    }
    
    return {
      projects,
      activeTasks: [],
    };
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

  getTaskRepo(): TaskRepo {
    return this.taskRepo;
  }

  getConfigRepo(): ConfigRepo {
    return this.configRepo;
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
