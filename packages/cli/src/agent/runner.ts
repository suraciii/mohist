import { spawn, ChildProcess, SpawnOptions } from 'child_process';
import { Task, Issue } from '../types';
import { PromptTemplates } from './prompts';

export type SpawnFunction = (command: string, args: string[], options: SpawnOptions) => ChildProcess;

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
    _issueNumber: number,
    projectPath: string,
    _agentType: 'designer' | 'implementer',
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

      const agentProcess = this.spawnFn('opencode', args, {
        cwd: projectPath,
        stdio: ['ignore', 'pipe', 'pipe']
      });

      this.processes.set(taskId, agentProcess);

      let stdout = '';
      let stderr = '';

      agentProcess.stdout?.on('data', (data) => {
        stdout += data.toString();
        console.log(`[Agent ${taskId}] ${data.toString().trim()}`);
      });

      agentProcess.stderr?.on('data', (data) => {
        stderr += data.toString();
        console.error(`[Agent ${taskId} ERROR] ${data.toString().trim()}`);
      });

      const timeoutHandle = setTimeout(() => {
        console.log(`Agent ${taskId} timed out after ${this.timeout}ms`);
        this.killAgent(taskId);
        reject(new Error('Agent timeout'));
      }, this.timeout);

      agentProcess.on('close', (code) => {
        clearTimeout(timeoutHandle);
        this.processes.delete(taskId);

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
        console.error(`Agent ${taskId} error:`, error);
        reject(error);
      });
    });
  }

  killAgent(taskId: string): boolean {
    const process = this.processes.get(taskId);
    if (process) {
      process.kill('SIGTERM');
      this.processes.delete(taskId);
      console.log(`Killed agent ${taskId}`);
      return true;
    }
    return false;
  }

  killAll(): void {
    for (const [taskId, process] of this.processes) {
      process.kill('SIGTERM');
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

  async runDesignerAgent(issue: Issue, task: Task): Promise<void> {
    const prompt = PromptTemplates.getDesignerPrompt(
      issue.number,
      issue.title,
      issue.body
    );
    
    await this.spawnAgent(
      task.id,
      issue.number,
      task.projectId,
      'designer',
      prompt
    );
  }

  async runImplementerAgent(issue: Issue, task: Task, designPath: string): Promise<void> {
    const prompt = PromptTemplates.getImplementerPrompt(
      issue.number,
      issue.title,
      designPath
    );
    
    await this.spawnAgent(
      task.id,
      issue.number,
      task.projectId,
      'implementer',
      prompt
    );
  }
}
