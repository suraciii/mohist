import * as fs from 'fs';
import * as path from 'path';
import { Project } from '../types';
import { Log } from '../util/log';

const log = Log.create({ service: 'project' });

export class DirectoryDetector {
  static detectProject(projects: Project[]): Project | undefined {
    const currentDir = process.cwd();
    
    for (const project of projects) {
      if (currentDir.startsWith(project.path)) {
        return project;
      }
    }
    
    return this.detectProjectFromConfig();
  }

  private static detectProjectFromConfig(): Project | undefined {
    const currentDir = process.cwd();
    const configPath = path.join(currentDir, '.mohist', 'config.json');
    
    if (fs.existsSync(configPath)) {
      try {
        const configData = fs.readFileSync(configPath, 'utf-8');
        const config = JSON.parse(configData);
        
        return {
          id: config.id || 'detected',
          name: config.name || path.basename(currentDir),
          path: currentDir,
          baseBranch: config.baseBranch || 'main',
          createdAt: config.createdAt || new Date().toISOString(),
          updatedAt: config.updatedAt || new Date().toISOString()
        };
      } catch (error) {
        log.error('Failed to read project config', { configPath, error });
      }
    }
    
    return undefined;
  }

  static isInProjectDir(): boolean {
    const currentDir = process.cwd();
    const configPath = path.join(currentDir, '.mohist', 'config.json');
    return fs.existsSync(configPath);
  }
}
