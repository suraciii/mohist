import { spawn, ChildProcess, SpawnOptions } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { Task, Issue, Stage } from '../types';
import { PromptTemplates } from './prompts';
import { slugify } from '../utils/slugify';

export type SpawnFunction = (command: string, args: string[], options: SpawnOptions) => ChildProcess;

function getLogDir(projectName: string, issueNumber: number): string {
  const home = process.env.HOME || '';
  const slug = slugify(projectName);
  return path.join(home, '.mohist', 'projects', slug, 'logs', `issue-${issueNumber}`);
}

function stageToLogName(stage: Stage): string {
  return `agent-${stage}.log`;
}

export class AgentRunner {
  private processes: Map<string, ChildProcess> = new Map();
  private timeout: number;
  private spawnFn: SpawnFunction;

  constructor(timeout: number = 1800000, spawnFn?: SpawnFunction) {
    this.timeout = timeout;
    this.spawnFn = spawnFn ?? spawn;
  }

  async spawnAgent(
    taskId: string,
    worktreePath: string,
    projectName: string,
    issueNumber: number,
    stage: Stage,
    prompt: string
  ): Promise<void> {
    return new Promise((resolve, reject) => {
      const args = [
        'agent',
        '--local',
        '--message',
        prompt,
        '--timeout',
        String(this.timeout / 1000)
      ];

      console.log(`Spawning agent for task ${taskId}: opencode ${args.join(' ')}`);

      const logDir = getLogDir(projectName, issueNumber);
      fs.mkdirSync(logDir, { recursive: true });

      const logPath = path.join(logDir, stageToLogName(stage));
      let logStream: fs.WriteStream | null = null;
      try {
        logStream = fs.createWriteStream(logPath, { flags: 'a' });
      } catch {
        console.error(`Failed to open log file ${logPath}, continuing without file logging`);
      }

      const agentProcess = this.spawnFn('opencode', args, {
        cwd: worktreePath,
        stdio: ['ignore', 'pipe', 'pipe']
      });

      this.processes.set(taskId, agentProcess);

      let stdout = '';
      let stderr = '';

      agentProcess.stdout?.on('data', (data) => {
        const text = data.toString();
        stdout += text;
        console.log(`[Agent ${taskId}] ${text.trim()}`);
        logStream?.write(`[stdout] ${text}`);
      });

      agentProcess.stderr?.on('data', (data) => {
        const text = data.toString();
        stderr += text;
        console.error(`[Agent ${taskId} ERROR] ${text.trim()}`);
        logStream?.write(`[stderr] ${text}`);
      });

      const timeoutHandle = setTimeout(() => {
        console.log(`Agent ${taskId} timed out after ${this.timeout}ms`);
        logStream?.write(`\n[timeout] Agent timed out after ${this.timeout}ms\n`);
        logStream?.end();
        this.killAgent(taskId);
        reject(new Error('Agent timeout'));
      }, this.timeout);

      agentProcess.on('close', (code) => {
        clearTimeout(timeoutHandle);
        this.processes.delete(taskId);

        logStream?.write(`\n[exit] Agent exited with code ${code}\n`);
        logStream?.end();

        if (code === 0) {
          console.log(`Agent ${taskId} completed successfully`);
          resolve();
        } else {
          console.error(`Agent ${taskId} failed with code ${code}`);
          reject(new Error(`Agent failed with code ${code}: ${stderr}`));
        }
      });

      agentProcess.on('error', (error) => {
        clearTimeout(timeoutHandle);
        this.processes.delete(taskId);
        logStream?.write(`\n[error] ${error.message}\n`);
        logStream?.end();
        console.error(`Agent ${taskId} error:`, error);
        reject(error);
      });
    });
  }

  killAgent(taskId: string): boolean {
    const process = this.processes.get(taskId);
    if (process) {
      process.kill('SIGTERM');
      const killTimeout = setTimeout(() => {
        try {
          process.kill('SIGKILL');
        } catch {
          // process already exited
        }
      }, 5000);
      process.on('exit', () => clearTimeout(killTimeout));
      this.processes.delete(taskId);
      console.log(`Killed agent ${taskId}`);
      return true;
    }
    return false;
  }

  killAll(): void {
    for (const [taskId, process] of this.processes) {
      process.kill('SIGTERM');
      setTimeout(() => {
        try {
          process.kill('SIGKILL');
        } catch {
          // process already exited
        }
      }, 5000);
      console.log(`Killed agent ${taskId}`);
    }
    this.processes.clear();
  }

  getRunningCount(): number {
    return this.processes.size;
  }

  isRunning(taskId: string): boolean {
    return this.processes.has(taskId);
  }

  async runDesignerAgent(
    issue: Issue,
    task: Task,
    worktreePath: string,
    projectName: string
  ): Promise<void> {
    const prompt = PromptTemplates.getDesignerPrompt(
      issue.number,
      issue.title,
      issue.body
    );

    await this.spawnAgent(
      task.id,
      worktreePath,
      projectName,
      issue.number,
      task.stage,
      prompt
    );
  }

  async runImplementerAgent(
    issue: Issue,
    task: Task,
    designPath: string,
    worktreePath: string,
    projectName: string
  ): Promise<void> {
    const prompt = PromptTemplates.getImplementerPrompt(
      issue.number,
      issue.title,
      designPath
    );

    await this.spawnAgent(
      task.id,
      worktreePath,
      projectName,
      issue.number,
      task.stage,
      prompt
    );
  }
}
