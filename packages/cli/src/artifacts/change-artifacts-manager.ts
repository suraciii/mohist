import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { slugify } from '../utils/slugify';

const execFileAsync = promisify(execFile);

export interface Task {
  id: string;
  order: number;
  title: string;
  description: string;
  acceptanceCriteria?: string[];
  dependsOn?: string[];
  spec?: string;
  passes: boolean;
  attempts: number;
  error?: string | null;
}

export interface TasksFile {
  version: number;
  tasks: Task[];
}

export interface CommitHistory {
  hash: string;
  message: string;
  date: string;
  author: string;
}

export interface ChangeInfo {
  number: number;
  slug: string;
  path: string;
}

export interface ArtifactsConfig {
  autoCommit: boolean;
  commitMessageTemplate: string;
  archiveAfterDays: number;
  preserveHistory: boolean;
}

export class ChangeArtifactsManager {
  private projectPath: string;
  private changesDir: string;

  constructor(projectPath: string, _config: Partial<ArtifactsConfig> = {}) {
    this.projectPath = projectPath;
    this.changesDir = path.join(projectPath, 'openspec', 'changes');
  }

  private ensureDir(dirPath: string): void {
    if (!fs.existsSync(dirPath)) {
      fs.mkdirSync(dirPath, { recursive: true });
    }
  }

  private getChangeDirName(issueNumber: number, title: string): string {
    const slug = slugify(title);
    return `${issueNumber}-${slug}`;
  }

  createChangeDir(issueNumber: number, title: string): string {
    const changeName = this.getChangeDirName(issueNumber, title);
    const changePath = path.join(this.changesDir, changeName);

    this.ensureDir(this.changesDir);
    this.ensureDir(changePath);
    this.ensureDir(path.join(changePath, 'specs'));

    return changePath;
  }

  findChangeDir(issueNumber: number): string | null {
    if (!fs.existsSync(this.changesDir)) {
      return null;
    }

    const entries = fs.readdirSync(this.changesDir, { withFileTypes: true });
    const prefix = `${issueNumber}-`;
    const match = entries.find(e => e.isDirectory() && e.name.startsWith(prefix));
    
    if (!match) {
      return null;
    }

    return path.join(this.changesDir, match.name);
  }

  listChanges(): ChangeInfo[] {
    if (!fs.existsSync(this.changesDir)) {
      return [];
    }

    const entries = fs.readdirSync(this.changesDir, { withFileTypes: true });
    const changes: ChangeInfo[] = [];

    for (const entry of entries) {
      if (entry.isDirectory() && !entry.name.startsWith('archive')) {
        const numberPart = entry.name.split('-')[0];
        const number = parseInt(numberPart, 10);
        
        if (!isNaN(number)) {
          const slugStartIndex = numberPart.length + 1;
          changes.push({
            number,
            slug: entry.name.substring(slugStartIndex),
            path: path.join(this.changesDir, entry.name),
          });
        }
      }
    }

    return changes.sort((a, b) => a.number - b.number);
  }

  readProposal(issueNumber: number): string | null {
    return this.readArtifactByIssue(issueNumber, 'proposal.md');
  }

  readDesign(issueNumber: number): string | null {
    return this.readArtifactByIssue(issueNumber, 'design.md');
  }

  readSpecs(issueNumber: number): Array<{ name: string; content: string }> {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      return [];
    }

    const specsDir = path.join(changeDir, 'specs');
    if (!fs.existsSync(specsDir)) {
      return [];
    }

    const files = fs.readdirSync(specsDir).filter(f => f.endsWith('.md'));
    const specs: Array<{ name: string; content: string }> = [];

    for (const file of files) {
      const filePath = path.join(specsDir, file);
      const content = fs.readFileSync(filePath, 'utf-8');
      specs.push({ name: file.replace('.md', ''), content });
    }

    return specs;
  }

  readTasks(issueNumber: number): TasksFile | null {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      return null;
    }

    const tasksPath = path.join(changeDir, 'tasks.json');
    if (!fs.existsSync(tasksPath)) {
      return null;
    }

    try {
      const content = fs.readFileSync(tasksPath, 'utf-8');
      return JSON.parse(content) as TasksFile;
    } catch {
      return null;
    }
  }

  private readArtifactByIssue(issueNumber: number, artifactPath: string): string | null {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      return null;
    }

    const filePath = path.join(changeDir, artifactPath);
    if (!fs.existsSync(filePath)) {
      return null;
    }

    try {
      return fs.readFileSync(filePath, 'utf-8');
    } catch {
      return null;
    }
  }

  writeProposal(issueNumber: number, content: string): void {
    this.writeArtifactByIssue(issueNumber, 'proposal.md', content);
  }

  writeDesign(issueNumber: number, content: string): void {
    this.writeArtifactByIssue(issueNumber, 'design.md', content);
  }

  writeSpec(issueNumber: number, capability: string, content: string): void {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      throw new Error(`ChangeNotFoundError: Change directory for issue #${issueNumber} not found.\nExpected: ${this.changesDir}/{number}-{slug}/`);
    }

    const specName = `${capability}.md`;
    const specPath = path.join(changeDir, 'specs', specName);

    try {
      fs.writeFileSync(specPath, content, 'utf-8');
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      throw new Error(`Failed to write spec ${specName}: ${message}\nPath: ${specPath}`);
    }
  }

  writeTasks(issueNumber: number, tasks: TasksFile): void {
    this.writeArtifactByIssue(issueNumber, 'tasks.json', JSON.stringify(tasks, null, 2));
  }

  private writeArtifactByIssue(issueNumber: number, artifactPath: string, content: string): void {
    const changeDir = this.findChangeDir(issueNumber);
    
    if (!changeDir) {
      throw new Error(`ChangeNotFoundError: Change directory for issue #${issueNumber} not found.\nExpected: ${this.changesDir}/{number}-{slug}/`);
    }

    const filePath = path.join(changeDir, artifactPath);

    try {
      fs.writeFileSync(filePath, content, 'utf-8');
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      throw new Error(`Failed to write ${artifactPath}: ${message}\nPath: ${filePath}`);
    }
  }

  async commitChanges(issueNumber: number, message: string): Promise<void> {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      throw new Error(`ChangeNotFoundError: Change directory for issue #${issueNumber} not found.`);
    }

    const relPath = path.relative(this.projectPath, changeDir);

    try {
      await execFileAsync('git', ['-C', this.projectPath, 'add', relPath]);
      await execFileAsync('git', ['-C', this.projectPath, 'commit', '-m', message]);
    } catch (err) {
      const error = err as Error & { stdout?: string; stderr?: string };
      const msg = error.message || String(err);
      throw new Error(`Git commit failed: ${msg}\nstdout: ${error.stdout}\nstderr: ${error.stderr}`);
    }
  }

  async getHistory(issueNumber: number): Promise<CommitHistory[]> {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      return [];
    }

    const relPath = path.relative(this.projectPath, changeDir);

    try {
      const { stdout } = await execFileAsync('git', [
        '-C', this.projectPath,
        'log',
        '--format=%H|%s|%ad|%an',
        '--date=iso',
        '--', relPath
      ]);

      if (!stdout.trim()) {
        return [];
      }

      const lines = stdout.trim().split('\n');
      return lines.map(line => {
        const [hash, message, date, author] = line.split('|');
        return { hash, message, date, author };
      });
    } catch {
      return [];
    }
  }

  async archiveChange(issueNumber: number): Promise<void> {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      throw new Error(`ChangeNotFoundError: Change directory for issue #${issueNumber} not found.`);
    }

    const archiveDir = path.join(this.changesDir, 'archive');
    this.ensureDir(archiveDir);

    const changeName = path.basename(changeDir);
    const destPath = path.join(archiveDir, changeName);

    fs.renameSync(changeDir, destPath);
  }

  async restoreChange(issueNumber: number): Promise<void> {
    const archiveDir = path.join(this.changesDir, 'archive');

    if (!fs.existsSync(archiveDir)) {
      throw new Error(`Archive directory not found`);
    }

    const entries = fs.readdirSync(archiveDir, { withFileTypes: true });
    const prefix = `${issueNumber}-`;
    const match = entries.find(e => e.isDirectory() && e.name.startsWith(prefix));

    if (!match) {
      throw new Error(`Archived change for issue #${issueNumber} not found`);
    }

    const srcPath = path.join(archiveDir, match.name);
    const destPath = path.join(this.changesDir, match.name);

    fs.renameSync(srcPath, destPath);
  }

  exists(changeDir: string): boolean {
    return fs.existsSync(changeDir);
  }

  getChangeDir(issueNumber: number): string | null {
    return this.findChangeDir(issueNumber);
  }

  readArtifact(changeDir: string, artifactPath: string): string | null {
    const filePath = path.join(changeDir, artifactPath);
    if (!fs.existsSync(filePath)) {
      return null;
    }
    try {
      return fs.readFileSync(filePath, 'utf-8');
    } catch {
      return null;
    }
  }

  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean {
    const filePath = path.join(changeDir, artifactPath);
    try {
      this.ensureDir(path.dirname(filePath));
      fs.writeFileSync(filePath, content, 'utf-8');
      return true;
    } catch {
      return false;
    }
  }

  updateTaskPasses(
    issueNumber: number,
    taskId: string,
    passes: boolean,
    error?: string | null
  ): boolean {
    const tasksFile = this.readTasks(issueNumber);
    if (!tasksFile) {
      return false;
    }

    const task = tasksFile.tasks.find(t => t.id === taskId);
    if (!task) {
      return false;
    }

    task.passes = passes;
    if (error !== undefined) task.error = error;

    try {
      this.writeTasks(issueNumber, tasksFile);
      return true;
    } catch {
      return false;
    }
  }
}