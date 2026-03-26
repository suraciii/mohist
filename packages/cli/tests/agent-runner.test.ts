import { describe, it, expect, beforeEach, vi } from 'vitest';
import { EventEmitter } from 'events';
import { AgentRunner, SpawnFunction } from '../src/agent/runner';
import { Issue, Task, Stage, IssueStatus } from '../src/types';
import { SpawnOptions, ChildProcess } from 'child_process';
import * as fs from 'fs';

vi.mock('fs', () => ({
  ...vi.importActual('fs'),
  existsSync: vi.fn(() => true),
  mkdirSync: vi.fn(() => undefined),
  createWriteStream: vi.fn(() => ({
    write: vi.fn(),
    end: vi.fn(),
    on: vi.fn(),
    once: vi.fn(),
    emit: vi.fn(),
  })),
}));

function createMockChildProcess(): ChildProcess {
  const proc = new EventEmitter() as ChildProcess;
  proc.pid = 12345;
  proc.stdin = null;
  proc.stdout = new EventEmitter() as any;
  proc.stderr = new EventEmitter() as any;
  proc.kill = vi.fn(() => true);
  proc.disconnect = vi.fn();
  proc.unref = vi.fn();
  proc.ref = vi.fn();
  return proc;
}

function createMockSpawn() {
  const processes: ChildProcess[] = [];
  
  const mockSpawn: SpawnFunction = vi.fn(((_command: string, _args: string[], _options: SpawnOptions) => {
    const proc = createMockChildProcess();
    processes.push(proc);
    return proc;
  }) as SpawnFunction);

  return {
    mockSpawn,
    getLastProcess: () => processes[processes.length - 1],
    getAllProcesses: () => processes,
    simulateExit: (code: number = 0, proc = processes[processes.length - 1]) => {
      if (proc) proc.emit('close', code);
    },
    simulateError: (error: Error, proc = processes[processes.length - 1]) => {
      if (proc) proc.emit('error', error);
    },
    simulateStdout: (data: string, proc = processes[processes.length - 1]) => {
      if (proc?.stdout) proc.stdout.emit('data', Buffer.from(data));
    },
    simulateStderr: (data: string, proc = processes[processes.length - 1]) => {
      if (proc?.stderr) proc.stderr.emit('data', Buffer.from(data));
    }
  };
}

describe('AgentRunner', () => {
  let runner: AgentRunner;
  let mockSpawnHelper: ReturnType<typeof createMockSpawn>;

  const mockIssue: Issue = {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage: Stage.Designing,
    status: IssueStatus.Active,
    labels: [],
    projectId: 'test-project-id',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockTask: Task = {
    id: 'task-1',
    issueId: 'issue-1',
    projectId: 'test-project-id',
    stage: Stage.Designing,
    status: 'pending'
  };

  const worktreePath = '/home/user/.mohist/projects/test-project/worktrees/issue-1';
  const projectName = 'test-project';

  beforeEach(() => {
    vi.clearAllMocks();
    mockSpawnHelper = createMockSpawn();
    runner = new AgentRunner(30000, mockSpawnHelper.mockSpawn);
  });

  describe('constructor', () => {
    it('should create runner with default timeout', () => {
      const defaultRunner = new AgentRunner();
      expect(defaultRunner).toBeDefined();
    });

    it('should create runner with custom timeout', () => {
      const customRunner = new AgentRunner(60000);
      expect(customRunner).toBeDefined();
    });

    it('should accept custom spawn function', () => {
      const customSpawn = vi.fn();
      const customRunner = new AgentRunner(30000, customSpawn);
      expect(customRunner).toBeDefined();
    });
  });

  describe('spawnAgent', () => {
    it('should spawn process with correct arguments', async () => {
      const promise = runner.spawnAgent(
        'task-1',
        worktreePath,
        projectName,
        1,
        Stage.Designing,
        'Test prompt'
      );

      expect(mockSpawnHelper.mockSpawn).toHaveBeenCalledWith(
        'opencode',
        [
          'agent',
          '--local',
          '--message',
          'Test prompt',
          '--timeout',
          '30'
        ],
        {
          cwd: worktreePath,
          stdio: ['ignore', 'pipe', 'pipe']
        }
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should use worktree path as cwd', async () => {
      const promise = runner.spawnAgent(
        'task-1',
        worktreePath,
        projectName,
        1,
        Stage.Designing,
        'prompt'
      );

      expect(mockSpawnHelper.mockSpawn).toHaveBeenCalledWith(
        'opencode',
        expect.anything(),
        expect.objectContaining({
          cwd: worktreePath
        })
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should create log directory and write log file', async () => {
      const promise = runner.spawnAgent(
        'task-1',
        worktreePath,
        projectName,
        1,
        Stage.Designing,
        'prompt'
      );

      expect(fs.mkdirSync).toHaveBeenCalledWith(
        expect.stringContaining('logs/issue-1'),
        { recursive: true }
      );
      expect(fs.createWriteStream).toHaveBeenCalledWith(
        expect.stringContaining('agent-designing.log'),
        { flags: 'a' }
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should resolve on successful exit', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      mockSpawnHelper.simulateExit(0);

      await expect(promise).resolves.toBeUndefined();
    });

    it('should reject on non-zero exit code', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      mockSpawnHelper.simulateStderr('Error occurred');
      mockSpawnHelper.simulateExit(1);

      await expect(promise).rejects.toThrow('Agent failed with code 1');
    });

    it('should reject on process error', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      mockSpawnHelper.simulateError(new Error('Spawn failed'));

      await expect(promise).rejects.toThrow('Spawn failed');
    });

    it('should capture stdout data', async () => {
      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      mockSpawnHelper.simulateStdout('Agent output\n');
      mockSpawnHelper.simulateExit(0);

      await promise;
      expect(consoleSpy).toHaveBeenCalledWith(expect.stringContaining('Agent output'));
      consoleSpy.mockRestore();
    });

    it('should capture stderr data', async () => {
      const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      mockSpawnHelper.simulateStderr('Warning message\n');
      mockSpawnHelper.simulateExit(0);

      await promise;
      expect(consoleErrorSpy).toHaveBeenCalledWith(expect.stringContaining('Warning message'));
      consoleErrorSpy.mockRestore();
    });

    it('should use different log file for implementing stage', async () => {
      const promise = runner.spawnAgent(
        'task-1',
        worktreePath,
        projectName,
        1,
        Stage.Implementing,
        'prompt'
      );

      expect(fs.createWriteStream).toHaveBeenCalledWith(
        expect.stringContaining('agent-implementing.log'),
        { flags: 'a' }
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });
  });

  describe('killAgent', () => {
    it('should return false for non-existent agent', () => {
      const killed = runner.killAgent('non-existent');
      expect(killed).toBe(false);
    });

    it('should kill running agent', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');
      const proc = mockSpawnHelper.getLastProcess();

      expect(runner.isRunning('task-1')).toBe(true);

      const killed = runner.killAgent('task-1');
      expect(killed).toBe(true);
      expect(proc?.kill).toHaveBeenCalledWith('SIGTERM');
      expect(runner.isRunning('task-1')).toBe(false);

      mockSpawnHelper.simulateExit(0);
      await promise;
    });
  });

  describe('getRunningCount', () => {
    it('should return 0 when no agents are running', () => {
      expect(runner.getRunningCount()).toBe(0);
    });

    it('should return correct count of running agents', async () => {
      const promise1 = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt1');
      const promise2 = runner.spawnAgent('task-2', worktreePath, projectName, 2, Stage.Designing, 'prompt2');

      expect(runner.getRunningCount()).toBe(2);

      mockSpawnHelper.simulateExit(0, mockSpawnHelper.getAllProcesses()[0]);
      mockSpawnHelper.simulateExit(0, mockSpawnHelper.getAllProcesses()[1]);

      await Promise.all([promise1, promise2]);
    });
  });

  describe('isRunning', () => {
    it('should return false for non-running task', () => {
      expect(runner.isRunning('task-1')).toBe(false);
    });

    it('should return true for running task', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');

      expect(runner.isRunning('task-1')).toBe(true);

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should return false after task completes', async () => {
      const promise = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt');
      mockSpawnHelper.simulateExit(0);
      await promise;

      expect(runner.isRunning('task-1')).toBe(false);
    });
  });

  describe('killAll', () => {
    it('should not throw when no agents are running', () => {
      expect(() => runner.killAll()).not.toThrow();
    });

    it('should kill all running agents', async () => {
      const promise1 = runner.spawnAgent('task-1', worktreePath, projectName, 1, Stage.Designing, 'prompt1');
      const promise2 = runner.spawnAgent('task-2', worktreePath, projectName, 2, Stage.Designing, 'prompt2');

      const processes = mockSpawnHelper.getAllProcesses();

      runner.killAll();

      expect(processes[0].kill).toHaveBeenCalledWith('SIGTERM');
      expect(processes[1].kill).toHaveBeenCalledWith('SIGTERM');
      expect(runner.getRunningCount()).toBe(0);

      mockSpawnHelper.simulateExit(0, processes[0]);
      mockSpawnHelper.simulateExit(0, processes[1]);
      await Promise.allSettled([promise1, promise2]);
    });
  });

  describe('runDesignerAgent', () => {
    it('should spawn agent with designer prompt', async () => {
      const promise = runner.runDesignerAgent(mockIssue, mockTask, worktreePath, projectName);

      expect(mockSpawnHelper.mockSpawn).toHaveBeenCalledWith(
        'opencode',
        expect.arrayContaining([
          'agent',
          '--local',
          '--message',
          expect.stringContaining('Designer Agent'),
          '--timeout',
          '30'
        ]),
        expect.objectContaining({
          cwd: worktreePath,
        })
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should include issue number and title in prompt', async () => {
      const promise = runner.runDesignerAgent(mockIssue, mockTask, worktreePath, projectName);

      const callArgs = mockSpawnHelper.mockSpawn.mock.calls[0][1];
      const promptArg = callArgs[callArgs.indexOf('--message') + 1];

      expect(promptArg).toContain('#1');
      expect(promptArg).toContain('Test Issue');

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should use worktree path as cwd', async () => {
      const promise = runner.runDesignerAgent(mockIssue, mockTask, worktreePath, projectName);

      expect(mockSpawnHelper.mockSpawn).toHaveBeenCalledWith(
        'opencode',
        expect.anything(),
        expect.objectContaining({
          cwd: worktreePath,
        })
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });
  });

  describe('runImplementerAgent', () => {
    it('should spawn agent with implementer prompt', async () => {
      const promise = runner.runImplementerAgent(mockIssue, mockTask, '/path/to/design.md', worktreePath, projectName);

      expect(mockSpawnHelper.mockSpawn).toHaveBeenCalledWith(
        'opencode',
        expect.arrayContaining([
          'agent',
          '--local',
          '--message',
          expect.stringContaining('Implementer Agent'),
          '--timeout',
          '30'
        ]),
        expect.objectContaining({
          cwd: worktreePath,
        })
      );

      mockSpawnHelper.simulateExit(0);
      await promise;
    });

    it('should include design path in prompt', async () => {
      const promise = runner.runImplementerAgent(mockIssue, mockTask, '/path/to/design.md', worktreePath, projectName);

      const callArgs = mockSpawnHelper.mockSpawn.mock.calls[0][1];
      const promptArg = callArgs[callArgs.indexOf('--message') + 1];

      expect(promptArg).toContain('/path/to/design.md');

      mockSpawnHelper.simulateExit(0);
      await promise;
    });
  });

  describe('prompt generation', () => {
    it('should have PromptTemplates for designer', async () => {
      const { PromptTemplates } = await import('../src/agent/prompts');
      const prompt = PromptTemplates.getDesignerPrompt(1, 'Test Title', 'Test Body');
      expect(prompt).toContain('Designer Agent');
      expect(prompt).toContain('#1');
      expect(prompt).toContain('Test Title');
    });

    it('should have PromptTemplates for implementer', async () => {
      const { PromptTemplates } = await import('../src/agent/prompts');
      const prompt = PromptTemplates.getImplementerPrompt(1, 'Test Title', 'design.md');
      expect(prompt).toContain('Implementer Agent');
      expect(prompt).toContain('#1');
      expect(prompt).toContain('design.md');
    });

    it('should have PromptTemplates for reviewer', async () => {
      const { PromptTemplates } = await import('../src/agent/prompts');
      const prompt = PromptTemplates.getReviewerPrompt(1, 'Test PR');
      expect(prompt).toContain('Reviewer Agent');
      expect(prompt).toContain('#1');
    });
  });
});
