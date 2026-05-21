import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createDefaultTaskHandlerRegistry } from '../../../src/workflow/tasks';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { ExecutableTask, RalphTaskInput } from '../../../src/workflow/tasks';
import type { OpenSpecChange } from '../../../src/openspec/detector';

let tempDir: string;

function createStageContext(): StageContext {
  return {
    issue: { id: 'issue-42', number: 42, title: 'Test Issue', body: 'Test body', projectId: 'proj-1' },
    acpOptions: { cwd: '/tmp', timeout: 600000 } as any,
    worktreeManager: undefined as any,
    emit: vi.fn(),
    log: vi.fn(),
  };
}

function createChange(): OpenSpecChange {
  const changeDir = path.join(tempDir, 'openspec', 'changes', 'test');
  fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });
  fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test Proposal');
  fs.writeFileSync(path.join(changeDir, 'design.md'), '# Test Design');
  fs.writeFileSync(
    path.join(changeDir, 'tasks.json'),
    JSON.stringify({
      version: 1,
      tasks: [{ id: 'T-001', title: 'Test Task', description: 'desc', passes: false, attempts: 0, order: 1 }],
    }),
  );

  return {
    changePath: changeDir,
    tasksPath: path.join(changeDir, 'tasks.json'),
    sessionMemoriesPath: path.join(changeDir, 'session-memories'),
    proposalPath: path.join(changeDir, 'proposal.md'),
    designPath: path.join(changeDir, 'design.md'),
    specsPath: path.join(changeDir, 'specs'),
  };
}

describe('createDefaultTaskHandlerRegistry', () => {
  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-task-registry-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('registers shared static task handlers by default', () => {
    const registry = createDefaultTaskHandlerRegistry();
    expect(registry.get('agent-session')).toBeTypeOf('function');
    expect(registry.get('service-call')).toBeTypeOf('function');
    expect(registry.get('ralph-task')).toBeUndefined();
  });

  it('executes a ralph-task through the shared registry handler', async () => {
    const acpSessionRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
    const change = createChange();
    const registry = createDefaultTaskHandlerRegistry({
      ralphTask: {
        worktreePath: tempDir,
        acpSessionRunner,
      },
    });
    const handler = registry.get('ralph-task');

    const input: RalphTaskInput = {
      taskId: 'T-001',
      title: 'Test Task',
      task: {
        id: 'T-001',
        title: 'Test Task',
        description: 'desc',
        passes: false,
        attempts: 0,
        order: 1,
        error: null,
        dependsOn: [],
        durations: [],
      },
      change,
      totalTasks: 1,
      stage: 'build',
      attempt: 1,
    };
    const task: ExecutableTask = {
      taskId: 'T-001',
      title: 'Test Task',
      kind: 'ralph-task',
      input,
    };

    const result = await handler!(task, createStageContext());

    expect(result.status).toBe('completed');
    expect(acpSessionRunner).toHaveBeenCalledOnce();
    const persisted = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(persisted.tasks[0]).toMatchObject({ passes: true, attempts: 1, error: null });
  });
});
