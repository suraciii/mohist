import { Project, Issue, Task } from '../types';
import * as fs from 'fs';
import * as path from 'path';

export class StateManager {
  private dataDir: string;
  private projectsFile: string;

  constructor() {
    this.dataDir = path.join(process.env.HOME || '', '.crawlph');
    this.projectsFile = path.join(this.dataDir, 'projects.json');
  }

  loadProjects(): Project[] {
    if (!fs.existsSync(this.projectsFile)) {
      return [];
    }

    try {
      const data = fs.readFileSync(this.projectsFile, 'utf-8');
      return JSON.parse(data);
    } catch (error) {
      console.error('Failed to load projects:', error);
      return [];
    }
  }

  saveProjects(projects: Project[]): void {
    try {
      fs.writeFileSync(
        this.projectsFile,
        JSON.stringify(projects, null, 2),
        'utf-8'
      );
    } catch (error) {
      console.error('Failed to save projects:', error);
      throw error;
    }
  }

  async recoverIssuesFromGitHub(project: Project): Promise<Issue[]> {
    console.log(`Recovering issues for project ${project.name} from GitHub...`);
    
    return [];
  }

  async recoverTasksFromState(issues: Issue[]): Promise<Task[]> {
    console.log('Recovering tasks from issue states...');
    
    const tasks: Task[] = [];
    
    for (const issue of issues) {
      if (issue.status === 'active') {
        tasks.push({
          id: `recovered-${issue.number}`,
          issueNumber: issue.number,
          projectId: issue.projectId,
          stage: issue.stage,
          status: 'pending',
          startedAt: new Date().toISOString()
        });
      }
    }

    return tasks;
  }
}
