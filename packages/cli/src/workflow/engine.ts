import { Task, IssueStatus } from '../types';
import { TaskRepo, IssueRepo, ProjectRepo } from '../db';
import { AgentRunner } from '../agent/runner';
import { WorktreeManager } from '../git/worktree-manager';
import { getStageHandler, StageHandlerContext } from './stage-handlers';
import { getNextStage, requiresUserApproval, isTerminalStage } from './issue-workflow';

export interface EngineConfig {
  maxConcurrentAgents: number;
  pollInterval: number;
}

export class WorkflowEngine {
  private workers: Promise<void>[] = [];
  private running: boolean = false;
  private worktreeRegistry: Map<string, string> = new Map();
  private issueTaskMap: Map<string, string> = new Map();

  constructor(
    private taskRepo: TaskRepo,
    private issueRepo: IssueRepo,
    private projectRepo: ProjectRepo,
    private agentRunner: AgentRunner,
    private worktreeManager: WorktreeManager,
    private config: EngineConfig
  ) {}

  async start(): Promise<void> {
    await this.recoverWorktreeRegistry();
    this.recoverIssueTaskMap();
    this.running = true;
    for (let i = 0; i < this.config.maxConcurrentAgents; i++) {
      this.workers.push(this.workerLoop(i));
    }
    console.log(`WorkflowEngine started with ${this.config.maxConcurrentAgents} workers`);
  }

  async stop(timeoutMs = 30000): Promise<void> {
    console.log('WorkflowEngine stopping...');
    this.running = false;

    const workersDone = Promise.all(this.workers).then(() => 'stopped' as const);
    const timeoutPromise = new Promise<'timeout'>(resolve =>
      setTimeout(() => resolve('timeout'), timeoutMs)
    );

    const result = await Promise.race([workersDone, timeoutPromise]);

    if (result === 'timeout') {
      console.log(`Graceful stop timed out after ${timeoutMs}ms, force killing agents...`);
      this.agentRunner.killAll();
      const runningTasks = this.taskRepo.findRunning();
      for (const task of runningTasks) {
        this.taskRepo.updateStatus(task.id, 'failed', 'Server stopped');
      }
    }

    this.workers = [];
    console.log('WorkflowEngine stopped');
  }

  killAgentByIssueId(issueId: string): void {
    const taskId = this.issueTaskMap.get(issueId);
    if (!taskId) return;

    const task = this.taskRepo.findById(taskId);
    if (!task || task.status !== 'running') return;

    this.taskRepo.updateStatus(taskId, 'failed', 'user_paused');
    this.agentRunner.killAgent(taskId);
    this.issueTaskMap.delete(issueId);
    console.log(`Killed agent for issue ${issueId} (task: ${taskId})`);
  }

  registerWorktree(issueId: string, worktreePath: string): void {
    this.worktreeRegistry.set(issueId, worktreePath);
  }

  unregisterWorktree(issueId: string): void {
    this.worktreeRegistry.delete(issueId);
  }

  getWorktreePath(issueId: string): string | undefined {
    return this.worktreeRegistry.get(issueId);
  }

  getActiveWorkerCount(): number {
    return this.issueTaskMap.size;
  }

  getPendingTaskCount(): number {
    return this.taskRepo.countPending();
  }

  private async workerLoop(workerId: number): Promise<void> {
    while (this.running) {
      try {
        const task = this.taskRepo.findAndClaim();
        if (!task) {
          await this.sleep(this.config.pollInterval);
          continue;
        }

        console.log(`Worker ${workerId} claimed task ${task.id} (issue: ${task.issueId}, stage: ${task.stage})`);
        this.issueTaskMap.set(task.issueId, task.id);

        try {
          await this.executeTask(task);
        } catch (error) {
          const currentTask = this.taskRepo.findById(task.id);
          if (currentTask && currentTask.status !== 'failed') {
            this.taskRepo.updateStatus(task.id, 'failed', String(error));
            const issue = this.issueRepo.findById(task.issueId);
            if (issue && issue.status === IssueStatus.Active) {
              this.issueRepo.updateStatus(task.issueId, IssueStatus.Blocked);
              console.error(`Issue ${task.issueId} blocked due to task failure: ${error}`);
            }
          }
        } finally {
          this.issueTaskMap.delete(task.issueId);
        }
      } catch (error) {
        console.error(`Worker ${workerId} error:`, error);
        await this.sleep(this.config.pollInterval);
      }
    }
  }

  private async executeTask(task: Task): Promise<void> {
    const issue = this.issueRepo.findById(task.issueId);
    if (!issue) throw new Error(`Issue ${task.issueId} not found`);

    const worktreePath = this.getWorktreePath(task.issueId);
    if (!worktreePath) throw new Error(`No worktree registered for issue ${task.issueId}`);

    const project = this.projectRepo.findById(task.projectId);
    if (!project) throw new Error(`Project ${task.projectId} not found`);

    const context: StageHandlerContext = {
      worktreePath,
      projectName: project.name,
    };

    const handler = getStageHandler(task.stage, this.agentRunner);
    await handler.execute(issue, task, context);

    this.taskRepo.updateStatus(task.id, 'completed');

    const freshIssue = this.issueRepo.findById(task.issueId);
    if (!freshIssue || freshIssue.status !== IssueStatus.Active) {
      console.log(`Issue ${task.issueId} no longer active after task completion, skipping stage advance`);
      return;
    }

    const nextStage = getNextStage(task.stage);
    if (!nextStage) return;

    this.issueRepo.updateStage(task.issueId, nextStage);

    if (!requiresUserApproval(nextStage) && !isTerminalStage(nextStage)) {
      this.taskRepo.create({
        issueId: task.issueId,
        projectId: task.projectId,
        stage: nextStage,
      });
      console.log(`Issue ${task.issueId} advanced to ${nextStage}, new task created`);
    } else {
      console.log(`Issue ${task.issueId} advanced to ${nextStage}`);
    }
  }

  private async recoverWorktreeRegistry(): Promise<void> {
    const projects = this.projectRepo.findAll();
    for (const project of projects) {
      try {
        const worktrees = await this.worktreeManager.list(project.path);
        for (const wt of worktrees) {
          const issue = this.issueRepo.findByNumber(project.id, wt.issueNumber);
          if (issue) {
            this.worktreeRegistry.set(issue.id, wt.worktreePath);
          }
        }
      } catch {
        // skip projects that aren't git repos
      }
    }
    if (this.worktreeRegistry.size > 0) {
      console.log(`Recovered ${this.worktreeRegistry.size} worktree(s) from disk`);
    }
  }

  private recoverIssueTaskMap(): void {
    const runningTasks = this.taskRepo.findRunning();
    for (const task of runningTasks) {
      this.issueTaskMap.set(task.issueId, task.id);
    }
    if (runningTasks.length > 0) {
      console.log(`Recovered ${runningTasks.length} running task mapping(s)`);
    }
  }

  private sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }
}
