import { Project } from '../types';
import { ProjectRepo, ConfigRepo } from '../db';

export interface CreateProjectData {
  name: string;
  path: string;
}

export class ProjectService {
  private currentProjectId: string | null = null;
  private static CURRENT_PROJECT_KEY = 'currentProjectId';

  constructor(
    private projectRepo: ProjectRepo,
    private configRepo: ConfigRepo
  ) {
    this.loadCurrentProject();
  }

  private loadCurrentProject(): void {
    const savedId = this.configRepo.get(ProjectService.CURRENT_PROJECT_KEY);
    if (savedId && this.projectRepo.findById(savedId)) {
      this.currentProjectId = savedId;
    }
  }

  create(data: CreateProjectData): Project {
    const existing = this.projectRepo.findByName(data.name);
    if (existing) {
      throw new Error(`Project "${data.name}" already exists`);
    }
    
    const existingByPath = this.projectRepo.findByPath(data.path);
    if (existingByPath) {
      throw new Error(`Path "${data.path}" is already used by project "${existingByPath.name}"`);
    }
    
    return this.projectRepo.create({
      name: data.name,
      path: data.path,
    });
  }

  getById(id: string): Project | null {
    return this.projectRepo.findById(id);
  }

  getByName(name: string): Project | null {
    return this.projectRepo.findByName(name);
  }

  getByPath(path: string): Project | null {
    return this.projectRepo.findByPath(path);
  }

  getAll(): Project[] {
    return this.projectRepo.findAll();
  }

  getCurrent(): Project | null {
    if (!this.currentProjectId) return null;
    return this.projectRepo.findById(this.currentProjectId);
  }

  setCurrent(project: Project): void {
    this.currentProjectId = project.id;
    this.configRepo.set(ProjectService.CURRENT_PROJECT_KEY, project.id);
  }

  setCurrentByName(name: string): Project | null {
    const project = this.projectRepo.findByName(name);
    if (!project) return null;
    this.setCurrent(project);
    return project;
  }

  clearCurrent(): void {
    this.currentProjectId = null;
    this.configRepo.delete(ProjectService.CURRENT_PROJECT_KEY);
  }

  delete(id: string): boolean {
    const project = this.projectRepo.findById(id);
    if (!project) return false;
    
    if (this.currentProjectId === id) {
      this.clearCurrent();
    }
    
    return this.projectRepo.delete(id);
  }

  deleteByName(name: string): boolean {
    const project = this.projectRepo.findByName(name);
    if (!project) return false;
    return this.delete(project.id);
  }

  count(): number {
    return this.projectRepo.count();
  }

  exists(name: string): boolean {
    return this.projectRepo.findByName(name) !== null;
  }
}
