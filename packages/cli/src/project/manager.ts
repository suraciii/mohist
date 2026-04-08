import { Project } from '../types';
import { v4 as uuidv4 } from 'uuid';

export class ProjectManager {
  private projects: Map<string, Project> = new Map();
  private currentProject: string | null = null;

  constructor(projects: Project[] = []) {
    projects.forEach(p => this.projects.set(p.id, p));
  }

  create(name: string, projectPath: string): Project {
    const id = uuidv4();
    const project: Project = {
      id,
      name,
      path: projectPath,
      baseBranch: 'main',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    this.projects.set(id, project);
    console.log(`Project created: ${name} (${projectPath})`);
    
    return project;
  }

  list(): Project[] {
    return Array.from(this.projects.values());
  }

  get(name: string): Project | undefined {
    return Array.from(this.projects.values()).find(p => p.name === name);
  }

  delete(name: string): boolean {
    const project = this.get(name);
    if (!project) {
      return false;
    }

    this.projects.delete(project.id);
    
    if (this.currentProject === project.id) {
      this.currentProject = null;
    }

    console.log(`Project deleted: ${name}`);
    return true;
  }

  use(name: string): Project | undefined {
    const project = this.get(name);
    if (project) {
      this.currentProject = project.id;
      console.log(`Switched to project: ${name}`);
    }
    return project;
  }

  getCurrent(): Project | undefined {
    if (!this.currentProject) {
      return undefined;
    }
    return this.projects.get(this.currentProject);
  }

  setCurrentById(id: string): void {
    this.currentProject = id;
  }
}
