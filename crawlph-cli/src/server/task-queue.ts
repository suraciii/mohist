import { Task } from '../types';
import { v4 as uuidv4 } from 'uuid';

export class TaskQueue {
  private queue: Task[] = [];
  private running: Map<string, Task> = new Map();
  private maxConcurrent: number;

  constructor(maxConcurrent: number = 8) {
    this.maxConcurrent = maxConcurrent;
  }

  enqueue(issueNumber: number, projectId: string, stage: string): string {
    const task: Task = {
      id: uuidv4(),
      issueNumber,
      projectId,
      stage: stage as any,
      status: 'pending',
      startedAt: new Date().toISOString()
    };

    this.queue.push(task);
    console.log(`Task ${task.id} enqueued for issue #${issueNumber}`);
    
    return task.id;
  }

  dequeue(): Task | undefined {
    if (this.running.size >= this.maxConcurrent) {
      return undefined;
    }

    const task = this.queue.shift();
    if (task) {
      task.status = 'running';
      task.startedAt = new Date().toISOString();
      this.running.set(task.id, task);
      console.log(`Task ${task.id} started for issue #${task.issueNumber}`);
    }

    return task;
  }

  complete(taskId: string, error?: string): void {
    const task = this.running.get(taskId);
    if (task) {
      task.status = error ? 'failed' : 'completed';
      task.completedAt = new Date().toISOString();
      task.error = error;
      this.running.delete(taskId);
      console.log(`Task ${taskId} completed with status: ${task.status}`);
    }
  }

  getQueueLength(): number {
    return this.queue.length;
  }

  getRunningCount(): number {
    return this.running.size;
  }

  getPendingTasks(): Task[] {
    return this.queue;
  }

  getRunningTasks(): Task[] {
    return Array.from(this.running.values());
  }

  getAllTasks(): Task[] {
    return [...this.queue, ...Array.from(this.running.values())];
  }

  canStartNew(): boolean {
    return this.running.size < this.maxConcurrent;
  }

  clear(): void {
    this.queue = [];
    this.running.clear();
  }
}
