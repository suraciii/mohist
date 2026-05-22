import type {
  TaskHandler,
  CheckHandler,
  TaskLoader,
  WorkflowTaskInput,
  WorkflowCheckInput,
  TaskLoadInput,
  TaskLoadResult,
  TaskResult,
  CheckResult,
} from '@mohist/workflow';
import type { Issue } from '../../types';

export interface MohistHandlerContext {
  worktreePath: string;
  issue: Issue;
  projectId: string;
}

export function createMohistTaskHandlers(_ctx: MohistHandlerContext): Record<string, TaskHandler> {
  return {
    'mohist/agent': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: Agent task ${input.id}` }; } },
    'mohist/check/ai-review': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: AI review ${input.id}` }; } },
    'mohist/rebase': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: Rebase ${input.id}` }; } },
    'mohist/openspec-sync': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: OpenSpec sync ${input.id}` }; } },
    'mohist/archive-change': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: Archive change ${input.id}` }; } },
    'mohist/merge': { async run(input: WorkflowTaskInput): Promise<TaskResult> { return { status: 'failed', reason: `TODO: Merge ${input.id}` }; } },
  };
}

export function createMohistCheckHandlers(ctx: MohistHandlerContext): Record<string, CheckHandler> {
  return {
    'mohist/health-gate': createHealthGateCheck(ctx),
    'mohist/artifact-exists': createArtifactExistsCheck(ctx),
    'mohist/marker': { async run(input: WorkflowCheckInput): Promise<CheckResult> { return { name: input.name, status: 'fail', message: `TODO: Marker ${input.name}` }; } },
    'mohist/merge-ready': { async run(input: WorkflowCheckInput): Promise<CheckResult> { return { name: input.name, status: 'fail', message: `TODO: Merge readiness ${input.name}` }; } },
    'mohist/approval': { async run(input: WorkflowCheckInput): Promise<CheckResult> { return { name: input.name, status: 'pending', message: 'Waiting for approval' }; } },
  };
}

export function createMohistTaskLoaders(_ctx: MohistHandlerContext): Record<string, TaskLoader> {
  return {
    'mohist/openspec-tasks': {
      async load(_input: TaskLoadInput): Promise<TaskLoadResult> { return { state: 'empty' }; },
    },
  };
}

function createHealthGateCheck(ctx: MohistHandlerContext): CheckHandler {
  return {
    async run(input: WorkflowCheckInput): Promise<CheckResult> {
      const command = input.with?.command as string | undefined;
      const timeout = (input.with?.timeout as number) ?? 300_000;
      if (!command) return { name: input.name, status: 'fail', message: 'Health gate requires command' };
      try {
        const { execFile } = await import('child_process');
        const { promisify } = await import('util');
        const execFileAsync = promisify(execFile);
        await execFileAsync('sh', ['-c', command], { cwd: ctx.worktreePath, timeout, maxBuffer: 10 * 1024 * 1024 });
        return { name: input.name, status: 'pass', message: 'Health gate passed' };
      } catch (err: any) {
        return { name: input.name, status: 'fail', message: `${command} failed`, output: { error: err.message } };
      }
    },
  };
}

function createArtifactExistsCheck(_ctx: MohistHandlerContext): CheckHandler {
  return {
    async run(input: WorkflowCheckInput): Promise<CheckResult> {
      const path = input.with?.path as string | undefined;
      if (!path) return { name: input.name, status: 'fail', message: 'Artifact path required' };
      try {
        const fs = await import('fs');
        if (fs.existsSync(path)) return { name: input.name, status: 'pass', message: `Artifact exists: ${path}` };
        return { name: input.name, status: 'fail', message: `Artifact not found: ${path}` };
      } catch {
        return { name: input.name, status: 'fail', message: `Cannot check artifact: ${path}` };
      }
    },
  };
}
