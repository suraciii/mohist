import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { slugify } from '../utils/slugify';

const execFileAsync = promisify(execFile);

export type TaskStatusValue = 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped';

export interface PrdTaskStatus {
  status: TaskStatusValue;
  startedAt?: string;
  completedAt?: string;
  attempts?: number;
  error?: string;
}

export interface PrdTask {
  id: string;
  order?: number;
  capability?: string;
  requirement_ref?: string;
  title: string;
  description: string;
  acceptance_criteria?: string[];
  dependencies?: string[];
  estimated_effort?: string;
  spec_file?: string;
  status?: TaskStatusValue;
  startedAt?: string;
  completedAt?: string;
  attempts?: number;
  error?: string;
}

export interface PrdJson {
  version?: string;
  change_id?: string;
  issue_reference?: string;
  generated_at?: string;
  tasks: PrdTask[];
  metadata?: {
    total_tasks?: number;
    capabilities_covered?: string[];
    session_memory_path?: string;
    task_status_path?: string;
  };
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
    this.changesDir = path.join(projectPath, '.mohist', 'changes');
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

  readPrd(issueNumber: number): PrdJson | null {
    const changeDir = this.findChangeDir(issueNumber);
    if (!changeDir) {
      return null;
    }

    const prdPath = path.join(changeDir, 'prd.json');
    if (!fs.existsSync(prdPath)) {
      return null;
    }

    try {
      const content = fs.readFileSync(prdPath, 'utf-8');
      return JSON.parse(content) as PrdJson;
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

  writePrd(issueNumber: number, prd: PrdJson): void {
    this.writeArtifactByIssue(issueNumber, 'prd.json', JSON.stringify(prd, null, 2));
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

  updateTaskStatus(
    issueNumber: number,
    taskId: string,
    status: PrdTaskStatus
  ): boolean {
    const prd = this.readPrd(issueNumber);
    if (!prd) {
      return false;
    }

    const task = prd.tasks.find(t => t.id === taskId);
    if (!task) {
      return false;
    }

    task.status = status.status;
    if (status.startedAt) task.startedAt = status.startedAt;
    if (status.completedAt) task.completedAt = status.completedAt;
    if (status.attempts !== undefined) task.attempts = status.attempts;
    if (status.error !== undefined) task.error = status.error;

    try {
      this.writePrd(issueNumber, prd);
      return true;
    } catch {
      return false;
    }
  }
}